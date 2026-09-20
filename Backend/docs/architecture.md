# Architecture

How the pieces fit, why they're split the way they are, and where the boundaries are. The other docs go deep on one part each: [failure-model.md](failure-model.md), [security-model.md](security-model.md), [scraper-testing.md](scraper-testing.md), [known-limitations.md](known-limitations.md).

## The shape

```
                        ┌──────────────────────────┐
  browser ──▶ Vercel ──▶│  ASP.NET Core API        │── reads/writes ──▶ PostgreSQL (Neon)
  (React)               │  (Back4app, no browser)  │                       ▲
                        └───────────┬──────────────┘                       │
                     repository_dispatch                                   │
                                    ▼                                      │
                        ┌──────────────────────────┐                       │
      cron 03:00 UTC ──▶│  GitHub Actions job      │── writes ─────────────┘
                        │  Playwright + real Chrome│── scrapes ──▶ noon.com
                        └──────────────────────────┘── alerts  ──▶ Telegram
```

Three .NET projects and a React app:

| Project | Owns | Does not know about |
|---|---|---|
| `NoonScraper.Data` | EF Core model, migrations, URL identity (`NoonUrl`), the price-history rules (`PriceHistoryAnalyzer`), the job state machine (`JobLifecycle`), subscriber notification (`SubscriptionNotifier`, `TelegramSender`) | ASP.NET, Playwright |
| `NoonScraper.Api` | HTTP: controllers, DTOs, rate limiting, request creation + dispatch (`JobRequestService`, `GitHubDispatchService`), the Telegram webhook | Playwright, anything that scrapes |
| `NoonScraper.Crawler` | Browser work: the scrapers, `ProductRecorder`, the three job entry points (`DailyCrawl`, `CrawlProductJob`, `CheckNowJob`) | HTTP, controllers |

The API and the crawler never call each other. They communicate through the database, and the only thing that crosses from the API to the crawler is a request id in a `repository_dispatch` payload. That is what makes each side testable alone.

## Why GitHub Actions is the browser worker (a deliberate choice, not a workaround I forgot to clean up)

The constraint is real and specific: the target site hard-blocks headless Chrome at the network layer (engineering log #2), so scraping needs a *headful* Chrome on a virtual display, and the free API hosting tier I use has no way to run that. Every alternative I looked at either costs money or adds moving parts that would be bigger than the application:

- **A queue and a worker service** (RabbitMQ, Redis, Azure Service Bus + a container that runs Chrome) is the textbook answer, and it would be the right one with a budget. It would also mean paying for and operating a second always-on service to do work that happens a few times a day.
- **Chrome inside the API container** stops working the moment the host has no display or memory for it, and it couples the web tier's stability to a browser that crashes.

GitHub Actions gives me an isolated, ephemeral, Chrome-capable machine per job for free, with a job queue (`repository_dispatch`), a scheduler (`cron`), logs, timeouts, and a per-run URL. What it doesn't give me is anything a real queue would: guaranteed delivery, exactly-once execution, cancellation I control, or a way to ask "did anyone pick this up?". So the design doesn't assume any of those. **The database is the source of truth, and the workflow is an unreliable trigger.** Everything a queue would guarantee is rebuilt explicitly and tested:

| A queue would give me | What I do instead |
|---|---|
| A record that work exists | A `ProductCrawlRequest` / `CheckNowRequest` row, written *before* dispatch |
| At-most-once processing | The worker claims the row with one conditional `UPDATE ... WHERE Status = 'Pending'`; a duplicate delivery claims nothing and exits |
| No two jobs for one product | A partial unique index: one active (Pending/Running) request per product |
| Redelivery / dead-letter | None automatically. A request that never gets picked up is expired to `Failed` by age; the user sees why and the daily crawl still covers the product |
| Visibility | Correlation id + GitHub run id + failure stage on the row; the run's name in the Actions list carries the ids |

This is a trade-off, and I'd swap it for a real queue the day there was budget for one. [Known limitations](known-limitations.md) lists what it costs.

## Job lifecycle

```
                dispatch fails ─────────────────────────────┐
                                                            ▼
 API: insert ──▶ Pending ──worker claims──▶ Running ──▶ Completed
                   │                          │
                   │  no worker within        │  worker dies / job times out
                   │  StaleAfter (15 min)     │  (StaleAfter after StartedAt)
                   └───────────▶ Failed ◀─────┘
```

`Failed` always carries a *stage* (`dispatch`, `budget`, `start`, `launch-browser`, `scrape`, `persist`, `cancelled`, `timeout`, `workflow`, `result`) and a message that is safe to show publicly. Every transition is a single conditional `UPDATE`, never read-modify-write, so the row count says who won when a worker, the expiry sweep and a second worker all act at once. [failure-model.md](failure-model.md) has the full table.

## One recorded reading = one transaction

`ProductRecorder` replaced the old static `ProductUpserter`. That class did a find-or-create and an append with nothing protecting it: two overlapping crawls of one product (the scheduled crawl and a user's on-demand one, or a retried job while its predecessor was still alive) could both insert the row, both record the same restock, or judge a discount against stale history; and a failure halfway through left a snapshot without its flag. It also mixed persistence with Telegram sending inside the same loop that scraped.

What it does now:

1. Take a Postgres advisory transaction lock keyed on the product URL (this also covers "the row doesn't exist yet", where there is nothing to `SELECT ... FOR UPDATE`).
2. In that one transaction: find-or-create the product, append the `PriceSnapshot`, run the restock and fake-discount rules against the history it can now see, write the event/flag. Commit.
3. After the commit, notify subscribers. Each alert is *claimed* with a conditional update before it is sent, so two crawls can't both send it, and a failed send releases the claim rather than advancing the baseline.

The API inserting a user-submitted URL is the one writer that doesn't take the lock; the unique index on `Products.Url` arbitrates, and the loser of that race retries once as an update.

Notifications live in `NoonScraper.Data` (not the Crawler) because they are persistence-shaped — they read and write subscription rows — and because the API's webhook handler shares the sender.

## Boundary review

What I checked, and what I changed:

- **Crawler ↔ Data:** the crawler used to own the upsert *and* the notification, so the rules that decide "is this a restock" lived next to Playwright code. The rules and notification now sit in `Data`; the crawler only adapts a scrape into `ProductRecorder.RecordAsync`. The three entry points share it, so restock/discount/notification logic still exists exactly once.
- **Api ↔ Data:** the API's `TelegramService` duplicated bot-token handling and message sending that the crawler also had. Both now use `TelegramSender`, which knows nothing about either host. Settings fallbacks (`Telegram:BotToken` → `TELEGRAM_BOT_TOKEN`, etc.) live in one `SettingsExtensions` class instead of being repeated at each call site.
- **Browser out of the tests:** parsing used to be inseparable from Playwright objects. The scrapers now hand the pure parts (`PriceText`, `ProductJsonLd`) plain strings, and the job classes take an `IScrapeSession` instead of a browser, so the failure logic is tested without Chrome. See [scraper-testing.md](scraper-testing.md).
- **Left as is, on purpose:** the API reads product/snapshot data with EF projections straight in the controller. There is no repository layer; with one data store and one consumer per query a repository would only add indirection.

## Where things are enforced

Rules that must hold under concurrency are enforced by the database, and application code only handles the case where the database says no:

| Rule | Enforced by |
|---|---|
| One row per product URL | unique index `Products.Url` |
| One active crawl request / one active check per product | partial unique indexes on `Status IN (Pending, Running)` |
| At most one scheduled crawl running | partial unique index on `CrawlRuns` where `Status = Running` |
| One subscription per (product, chat) | unique index |
| One restock event / discount flag per triggering snapshot | unique index on `TriggeringSnapshotId` |
| Children die with their product | cascading foreign keys |

Each has a test that provokes the violation against real PostgreSQL, including the concurrent case.
