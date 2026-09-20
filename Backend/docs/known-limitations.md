# Known limitations

Things I know are imperfect, ordered roughly by how much they'd matter in production. None are hidden elsewhere in the docs as strengths.

## Concurrency and reliability

- **The trigger is unreliable and there's no redelivery.** `repository_dispatch` can be lost, delayed, or delivered twice, and a runner can be killed without running any cleanup. Duplicates are harmless (claim-once). Losses are handled by *expiry*: a request nobody picks up is reported `Failed` after 15 minutes (`Jobs:StaleAfterMinutes`), not immediately, and nothing retries it. The user can start a new one; the daily crawl covers tracked products. A real queue would redeliver.
- **A worker that outlives its request loses its result.** If a run is slow enough that the API expires its request (15 min) before it finishes, the result is discarded so as not to contradict what the user was told. The on-demand workflows time out at 10 minutes, so this needs GitHub queueing delay plus a slow run.
- **A lost commit acknowledgement could record one reading twice.** `ProductRecorder` runs in a transaction behind a per-product advisory lock, and the connection uses Npgsql's retry-on-failure. If a commit succeeds but its acknowledgement is lost and the retry re-executes, one extra `PriceSnapshot` could be written. It's rare, would show as a duplicate point in a history log, and a duplicate snapshot is harmless to the rules. I haven't tried to force it.
- **A delisted product that already has history keeps failing daily.** It's counted as a failed product each run (below the 20 % threshold, so the run stays green with a warning annotation) until someone deactivates it; there's no automatic "give up after N failures".
- **The scheduled crawl's overlap protection is a database row**, not a workflow lock. `daily-crawl.yml` also has a `concurrency` group, but if a run is killed outright its `Running` row lingers until the next run marks it dead (after the time budget plus ten minutes), so a re-run started sooner than that exits without crawling.
- **`concurrency` groups in the on-demand workflows can drop a queued run.** GitHub keeps at most one pending run per group. The API allows only one active request per product, so it shouldn't happen, but if it did the dropped run's request would be closed by expiry.

## Security and abuse

- **No authentication.** By design for a public demo (see [security-model.md](security-model.md)); it means anyone can add products up to the caps and can exhaust the hourly dispatch budget, denying the feature to others until it rolls over.
- **Per-IP rate limits are forgeable and per-instance.** Behind Back4app's proxies the client IP comes from `X-Forwarded-For`; the in-process limiter resets on restart and doesn't span instances. The database-backed budgets are what protect the Actions quota, so this is about noisy clients, not cost.
- **There is no way to remove a product** a visitor shouldn't have added, short of editing the database.
- **Not independently reviewed or pen-tested.**

## Scraping

- **Fixture tests can't see Noon change its markup.** They're snapshots. A live-site canary would; I haven't built one ([scraper-testing.md](scraper-testing.md)).
- **Headful Chrome under xvfb defeats static bot detection only.** Behavioural detection, IP reputation of GitHub's runner ranges, or a CAPTCHA would stop the crawl; the failure would surface as failed categories and a red daily run, not as bad data.
- **The 20 % failure threshold is a judgement call**, not a measured one.
- Category pages are read as they render on first load (no scrolling or pagination), so products further down or on later pages of a category aren't tracked.

## Data and API

- **Price analysis is rule-based on what was crawled**: a discount claim is judged against the crawler's own history (90 days), so a product that has been crawled for a week has a short memory. Prices are stored in EGP without currency conversion.
- **Only the first result is trusted for "lowest offer"** in check-now (offers are sorted by price and the first is reported).
- **Offset pagination.** `page`/`pageSize` with a stable tie-break on `Id` is fine at this size; a very deep page would get slower, and rows added between two requests can shift a page.
- **The migration and a required deploy order.** The latest migration (`JobLifecycleAndIntegrityConstraints`) adds columns and constraints the new API and crawler need, and cleans up legacy rows first (it closes still-`Pending` requests, and removes duplicate flags/events). It is *tested* against a database built at the previous migration with representative legacy data. It has **not been applied to the production database** — I don't do that from a working session. Apply it first, then deploy the API, then the crawler workflows; deploying code first would fail on missing columns.

## Frontend

- **Contracts are declared by hand** (`schemas.ts`) and checked two ways: every response is validated at runtime, and CI parses fixtures pinned from the real API against them. There's no generator, so a new field needs an edit in the schema too — but forgetting it now fails a test instead of a user's page.
- **The progress bar is an estimate** from elapsed time; nothing reports real progress from inside the workflow.
- **Tests are jsdom + a fake `fetch`**, not a real browser against a running API. They cover behaviour and states, not layout.

## Operations

- **Logs are a workflow run's console output**, correlated by request / product / correlation / run id. There's no log aggregation, metrics or alerting; the daily crawl's health is one `CrawlRuns` row per run plus the workflow's red/green.
- **One API instance is assumed** wherever I've said "in-process".
