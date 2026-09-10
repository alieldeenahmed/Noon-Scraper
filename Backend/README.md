# Noon Scraper — Backend

An ASP.NET Core backend that tracks prices and stock on [noon.com](https://www.noon.com) (the largest e-commerce platform in the MENA region), with rule-based restock and fake-discount detection, and a live cross-merchant price comparison endpoint.

**Live:** [noonscraper-iptric6o.b4a.run](https://noonscraper-iptric6o.b4a.run/api/products) (e.g. `GET /api/products`) — this URL changes whenever the Back4app container needs recreating (see `docs/hosting.md`); if this link is dead, that's why.

## Why this project exists

This started as a scraper targeting TikTok, which got blocked outright by TikTok's anti-bot defenses almost immediately. Rather than abandon the idea, I pivoted to Noon: it has no official public API, but only moderate anti-bot protection compared to something like TikTok or LinkedIn — a realistic mid-difficulty target. Harder than scraping a site with an open JSON API (Reddit, Hacker News), easier than the hardest-to-automate platforms.

I deliberately kept the analysis rule-based rather than reaching for AI/ML — restock detection and fake-discount flagging are both plain threshold logic over price history, not model inference. That's intentional: it matches a "no-AI crawler" portfolio framing and keeps every decision the system makes explainable.

The goal isn't a scraper tech demo — it's meant to be an actually useful tool: price-transparency and deal-quality checking for online shoppers, not just infrastructure plumbing.

## Architecture

The backend is split into three .NET projects, each with a distinct job:

```
Backend/
├── NoonScraper.sln
├── NoonScraper.Data/       # EF Core models + AppDbContext + migrations - shared, no scraping logic
├── NoonScraper.Crawler/    # Console app: scrapes category pages + user-added product pages, run on a schedule
└── NoonScraper.Api/        # ASP.NET Core Web API: read endpoints, user URL submission, on-demand check-now
```

I split it this way because the Crawler and the API run in genuinely different execution contexts — the Crawler is meant to run once a day via a GitHub Actions cron job and exit, while the API runs continuously. Both need the same schema, so `NoonScraper.Data` holds the models and `AppDbContext` with no scraping code in it at all, and both `Crawler` and `Api` reference it.

### Data model

- **`Product`** — one row per tracked item. `Url` (normalized, no query string — see the engineering log for why that matters), `NoonProductId` (the SKU), `Name`, `MerchantName`, `Category`, `Rating`, `Source` (`Seed` or `UserAdded`), `IsActive`.
- **`PriceSnapshot`** — one row per crawl per product: `Price`, `Stock`, `DiscountPercent`, `CrawledAt`. This is the time series everything else is built on.
- **`NotificationSubscription`** — links a Telegram `chat_id` to a product, plus `LastNotifiedPrice`/`LastNotifiedAt` (the baseline the notifier compares each new price against). See `docs/telegram-notifications.md`.
- **`DiscountFlag`** — written when the fake-discount detector fires: the inflated "before" price, when it was seen, the discounted price, and the snapshot that triggered the flag.
- **`RestockEvent`** — written whenever a product flips from out-of-stock to in-stock: which snapshot confirmed it, and when. Previously this was only a console log line during the crawl with no queryable record at all.

### Tech stack

- **.NET 9** / ASP.NET Core Web API
- **EF Core** + **Npgsql** against **PostgreSQL on Neon** (chosen specifically because it's reachable from GitHub Actions runners — a local Postgres instance or Render's free Postgres, which expires after 90 days, wouldn't work for a scheduled cloud cron job)
- **Playwright** (real Chrome, not headless — see the engineering log) for scraping — this lives entirely in `NoonScraper.Crawler` now, not the API (see "Where scraping actually happens" below)
- **WSL2 (Ubuntu 24.04)** as the local dev/test environment for anything that launches a browser — required due to a Windows-specific blocker, also explained in the engineering log
- **Back4app Containers** hosts the API itself — a free tier with no credit card required, deploying `Backend/Dockerfile` straight from this GitHub repo. See `docs/hosting.md`.

### Where scraping actually happens

The API never launches a browser. Both the daily crawl and the on-demand "check now" comparison run as `NoonScraper.Crawler` invocations inside GitHub Actions — the only place a real, visible Chrome instance is available — and write their results to Neon. `POST /api/products/{id}/check-now` just creates a `CheckNowRequest` row and fires a `repository_dispatch` event to trigger `check-now.yml`; the client polls a second endpoint for the result. This is deliberate: keeping the API itself Chrome-free is what let it host on a free, card-free tier at all. See `docs/check-now.md` and the engineering log for why this changed from an earlier in-process design.

## Getting started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A [Neon](https://neon.tech) Postgres project
- **For running the Crawler specifically** (the only project that launches a browser): WSL2 with Ubuntu 24.04, Google Chrome, and Playwright's system dependencies installed. The API needs none of this — it never launches Chrome, even for `check-now` (see "Where scraping actually happens" above). See `docs/database-setup.md` and the engineering log for the full setup.

### Configuration

Connection strings are never committed. Locally, each project (`NoonScraper.Api`, `NoonScraper.Crawler`) reads its `ConnectionStrings:DefaultConnection` from **.NET user secrets**:

```bash
cd NoonScraper.Api   # or NoonScraper.Crawler
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<your Neon connection string>"
```

In CI (GitHub Actions) and in the Crawler's own config, the same key is read from an environment variable (`ConnectionStrings__DefaultConnection`) instead — the config system picks either up automatically. The API's hosting platform (Back4app) doesn't allow that double-underscore naming in its environment-variable UI, so the API additionally falls back to a flat `DATABASE` variable if `ConnectionStrings__DefaultConnection` isn't set — see `docs/hosting.md`.

Telegram notifications need two more secrets, set the same way (`Telegram:BotToken` / `Telegram:WebhookSecret` locally, or the flat `TELEGRAM_BOT_TOKEN` / `TELEGRAM_WEBHOOK_SECRET` anywhere that rejects the double-underscore naming) — see `docs/telegram-notifications.md`. Both are optional: with nothing configured, the app runs fine and notifications are just silently skipped.

### Running things

```bash
# Build everything
dotnet build

# Run the API
cd NoonScraper.Api
dotnet run

# Run the crawler (scrapes all 5 tracked categories + any user-added products)
cd NoonScraper.Crawler
dotnet run
```

The Crawler launches a real, visible (headful) Chrome instance — this only works from an environment with a display, which is why WSL2 is required locally (see the engineering log for exactly why headless doesn't work here). The API itself never does this, on any code path.

## API reference

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/products` | List tracked products, optional `?category=` filter, includes each product's latest price/stock/discount |
| `GET` | `/api/products/{id}` | Full detail for one product |
| `GET` | `/api/products/{id}/history` | Full price/stock history for one product |
| `GET` | `/api/products/{id}/discount-flags` | Every fake-discount flag raised for this product (prior high price, discounted price, when each was detected), most recent first |
| `GET` | `/api/products/{id}/restocks` | Every time this product went from out-of-stock to in-stock, most recent first |
| `POST` | `/api/products` | Submit a URL to track (`{ "url": "..." }`) — validates it's a noon.com link, normalizes it, rejects duplicates, inserts a bare record that the next crawl fills in |
| `POST` | `/api/products/{id}/check-now` | Kicks off a live, on-demand cross-merchant price check via GitHub Actions (see "Where scraping actually happens" above) — returns `202 Accepted` with a `requestId` immediately, doesn't scrape inline |
| `GET` | `/api/products/{id}/check-now/{requestId}` | Poll for that check's result — `Status` is `Pending`, `Completed`, or `Failed`; once `Completed`, `Offers` holds every seller's price sorted lowest first |
| `POST` | `/api/telegram/webhook` | Receives Telegram's webhook updates — not called directly by a frontend. Handles `/start <productId>` (subscribe) and `/stop` (unsubscribe from everything) sent to the bot. See `docs/telegram-notifications.md` |

## Current status

**Done:** data model, category-page crawling (5 categories: Mobiles, Laptops, Skin Care, Hair Care, Personal Care), user-submitted URL tracking with detail-page scraping, restock detection, fake-discount detection, on-demand cross-merchant check-now (via GitHub Actions), a scheduled GitHub Actions workflow that runs the crawl automatically once a day, a deployment of the API on Back4app, and Telegram notifications — subscribe/unsubscribe and restock/price-drop alerts, with the real webhook registered and verified against a live subscription (not just simulated locally).

**Not yet done:** a frontend deployment (the frontend itself is built — see `../Frontend/README.md` — but only runs locally via `npm run dev` so far), and Back4app's free tier has a real reliability gap worth knowing about before relying on the current URL staying put (see `docs/hosting.md`).

**Explicitly out of scope for V1:** crawling Noon's full catalog (only tracked products, seeded + user-submitted), anything behind login/checkout, a seasonal "best time to buy" predictor (needs months of data this project doesn't have yet), and treating cross-merchant comparison as a continuous background feature rather than an on-demand action.

See [`docs/database-setup.md`](docs/database-setup.md) for how the database is provisioned, [`docs/scheduled-crawl.md`](docs/scheduled-crawl.md) for how the daily automated crawl is set up, [`docs/check-now.md`](docs/check-now.md) for how the on-demand cross-merchant check works, [`docs/hosting.md`](docs/hosting.md) for how the API is deployed, [`docs/telegram-notifications.md`](docs/telegram-notifications.md) for how the notification subscribe flow and send logic work, and [`docs/engineering-log.md`](docs/engineering-log.md) for a full account of the technical obstacles this project ran into and how each one got resolved — including the anti-bot investigation, a local Windows tooling blocker, a real data-integrity bug, how the pricing/offer data actually gets extracted, and the hosting search that led to Back4app.
