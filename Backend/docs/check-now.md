# On-demand cross-merchant check ("check now")

## Why this isn't a simple synchronous endpoint

The obvious design for `POST /api/products/{id}/check-now` is to launch Chrome right there in the request handler, scrape, and return the result. That's what I originally built, and it worked — but it means the API itself needs a Chrome-capable, headful-browser-capable runtime, which rules out almost every free hosting tier (most either don't support launching a real browser at all, or only offer that on paid plans). Since the daily crawl already solved "run headful Chrome on a schedule" using GitHub Actions, the same approach works for on-demand checks too — the only thing that changes is what triggers the job.

## The flow

1. `POST /api/products/{id}/check-now` goes through `JobRequestService`: expire anything stale, and if the product already has an active check, **return that one** (so a double-click or a client retry doesn't start a second workflow run). Otherwise it inserts a `CheckNowRequest` (`Pending`, with a fresh correlation id) — a partial unique index guarantees one active check per product even if two requests race — and dispatches a `repository_dispatch` event to GitHub (`GitHubDispatchService`, a fine-grained PAT with `Actions: write`, stored in the API's own configuration, never committed). The payload carries the request id, product id and correlation id.
2. It returns `202 Accepted` with the request id. If the hourly dispatch budget is spent it returns `429` with `Retry-After`; if GitHub can't be reached after retries it returns `502` and the request is closed as `Failed` at stage `dispatch` instead of being left `Pending` for a worker that will never come.
3. GitHub Actions runs [`check-now.yml`](../../.github/workflows/check-now.yml) — the same Ubuntu 24.04 + Chrome + Playwright-deps setup as the daily crawl. The run's name contains the request id, product id and correlation id.
4. The workflow runs `NoonScraper.Crawler check-now <requestId>`. The job **claims** the request with one conditional `UPDATE ... WHERE Status = Pending` (a duplicate delivery claims nothing and exits), scrapes the product's page (`OfferScraper`, via an `IScrapeSession`), and completes it: `Completed` with `ResultJson` holding every seller's offer, or `Failed` with the stage it failed in and a message that is safe to show publicly. If the workflow dies before the crawler runs, a final `if: failure() || cancelled()` step closes the request (`fail-request`).
5. The client polls `GET /api/products/{id}/check-now/{requestId}` (looked up by *both* ids) until the status is `Completed` or `Failed`. Statuses are `Pending → Running → Completed | Failed`; a request nobody finishes within 15 minutes reads as `Failed` at stage `timeout`. The response includes the failure stage and a link to the GitHub run that handled it.

The full state machine, retries and stages are in [failure-model.md](failure-model.md).

## Why `OfferScraper`/`OfferResult`/`StealthBrowser` live in `NoonScraper.Crawler`, not `NoonScraper.Api`

They used to live in the API (`Services/`), from when check-now scraped in-process. Once the API stopped launching a browser at all, that code had no reason to reference Playwright, so it moved into the Crawler project — the only place that actually needs it. `OfferDto` (API) and `OfferResult` (Crawler) are deliberately two separate types with an identical shape rather than a shared reference between the two projects: `CheckNowRequest.ResultJson` is just a JSON string in the database, so the API deserializes it into its own DTO without needing to reference the Crawler assembly at all.

## Verifying it end-to-end

Confirmed against a real tracked product (an Anker USB-C charger with 3 known competing sellers): `POST /check-now` returned `202` with a request id, the `check-now.yml` run appeared in the Actions tab within seconds (triggered by `repository_dispatch`, not a manual run), and polling the result endpoint went `Pending` → `Completed` with the correct three offers, sorted lowest-price-first, matching what the original in-process version had returned for the same product.
