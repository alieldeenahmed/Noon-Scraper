# Crawling a product immediately on submit

## Why this exists

Without it, `POST /api/products` just inserts a bare row — `Url` and nothing else — and the product sits with no name, price, or stock until the next scheduled daily crawl reaches it, up to 24 hours later. For a "paste a link and watch it get tracked" flow, a day of silence isn't acceptable. This mirrors `check-now`'s reasoning almost exactly: the API has no browser of its own, so the actual scrape has to happen somewhere Chrome-capable, triggered on demand rather than waiting for a schedule.

## The flow

1. `POST /api/products` validates the URL with `NoonUrl.TryParse` (https only, exactly `noon.com` / `www.noon.com`, product-page path shape — see [security-model.md](security-model.md)), and rebuilds the canonical URL from the parsed parts. A product already tracked — the same URL, or the same product code in the same market under another language's page — returns `409` with its id. New-product limits (per hour, and in total) return `429`. Otherwise it inserts a bare `Product` (URL and product code only); if two people submit the same link at the same instant, the unique index on `Url` picks the winner and the loser gets the same `409` a sequential submission would.
2. It then asks `JobRequestService` for a crawl: a `ProductCrawlRequest` (`Pending`) is inserted — at most one active per product — and a `repository_dispatch` event (`crawl-product`) is fired. **The product is saved either way.** If the dispatch fails, or the hourly dispatch budget is spent, the request is recorded as `Failed` (stage `dispatch` / `budget`) and the `201` still comes back, with a `crawl` block saying so; the daily crawl will cover the product.
3. The `201 Created` returns immediately with the bare record and the `crawl` status.
4. GitHub Actions runs [`crawl-product.yml`](../../.github/workflows/crawl-product.yml), which invokes `NoonScraper.Crawler crawl-product <requestId>`. The job claims the request, scrapes the product's detail page (`ProductPageScraper`, JSON-LD based), records the reading through `ProductRecorder`, and completes the request — or fails it with a stage and a public-safe message.
5. The frontend's product page polls `GET /api/products/{id}` while `lastCrawledAt` is null *and the crawl hasn't failed*; the response's `crawl` field says `Pending` / `Running` / `Failed`, and a failure shows its reason, the stage, and a link to the workflow run. See "Frontend behavior" below.

## `ProductRecorder`: sharing the actual recording logic

The daily crawl, the re-crawl of user-added products, and this one-off crawl all do the same thing with a scraped page: find-or-create the `Product`, append a `PriceSnapshot`, run restock / fake-discount detection, notify Telegram subscribers. That is `ProductRecorder.RecordAsync` — it replaced the static `ProductUpserter`, which had no protection against two crawls of one product overlapping. It now runs each reading in one transaction behind a per-product advisory lock and sends notifications only after the commit; see [architecture.md](architecture.md).

## What happens if the dispatch fails

- A transient failure (network, 5xx, rate limiting) is retried up to three times with a short backoff before giving up.
- If it still fails, the request is closed as `Failed` at stage `dispatch`, the response says the crawl wasn't started, and the product is saved regardless.
- Even then the product is a `UserAdded`, `IsActive` row, which the daily crawl re-scrapes every run (see `scheduled-crawl.md`), so the worst case is that it catches up within 24 hours, as before this feature existed.

## A stale-deployment gotcha this feature actually ran into

See engineering log #14 — the first real test of this feature through the live site failed silently (bare row, zero matching Actions runs) not because of a bug in the feature itself, but because Back4app was still running the build from before the commit that added it. Worth knowing: if this ever seems broken again, check that the Actions tab shows *any* `crawl-product` run at all before debugging the workflow itself — zero runs means the dispatch never reached GitHub, which on this project has usually meant a stale deployment, not a code bug.

## Frontend behavior

`ProductDetailPage` polls every 4 seconds (capped at 5 minutes) while `lastCrawledAt` is null and the crawl request hasn't failed, and shows `CrawlProgressBar` — an elapsed-time estimate against a typical run (not a real step tracker; nothing reports actual progress from inside the GitHub Actions job), capped short of 100% until the crawl actually reports back. `AddProductForm` navigates straight to the new product's own page on submit, rather than leaving the user on the list page where a freshly-added, not-yet-crawled product would otherwise sort to the bottom under the default "last crawled" sort.

## Verifying it end-to-end

Confirmed by posting directly to the live API and watching a real `Crawl Product` workflow run appear in the Actions tab within seconds (not a manual/simulated run), and by submitting through the actual "track it" form and watching the browser navigate to the new product's page and the progress bar count down while `performance` entries showed a real `GET /api/products/{id}` request landing every ~4 seconds until the data arrived.
