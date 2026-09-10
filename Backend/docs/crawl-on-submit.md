# Crawling a product immediately on submit

## Why this exists

Without it, `POST /api/products` just inserts a bare row — `Url` and nothing else — and the product sits with no name, price, or stock until the next scheduled daily crawl reaches it, up to 24 hours later. For a "paste a link and watch it get tracked" flow, a day of silence isn't acceptable. This mirrors `check-now`'s reasoning almost exactly: the API has no browser of its own, so the actual scrape has to happen somewhere Chrome-capable, triggered on demand rather than waiting for a schedule.

## The flow

1. `POST /api/products` validates the URL, normalizes it, rejects duplicates, and inserts the bare `Product` row as before — then, best-effort, calls `GitHubDispatchService.TriggerCrawlProductAsync(product.Id)`, which fires a `repository_dispatch` event (`crawl-product`) the same way `check-now` does, just with a different event type and payload.
2. The `201 Created` response still returns immediately with the bare record — there's nothing to scrape yet, so nothing to return synchronously, same as `check-now`'s `202`.
3. GitHub Actions receives the dispatch and runs [`crawl-product.yml`](../../.github/workflows/crawl-product.yml), which invokes `NoonScraper.Crawler crawl-product <productId>` — a new CLI mode in `Program.cs` that loads that one product, scrapes its detail page (`ProductPageScraper`, the same JSON-LD-based scraper the daily crawl and user-added re-crawls use), and upserts the result.
4. The frontend's product page polls `GET /api/products/{id}` every few seconds while `lastCrawledAt` is still null, and shows a "crawling now" state with an elapsed-time progress bar (`CrawlProgressBar.tsx`) — see "Frontend behavior" below.

The dispatch call is wrapped in a `try`/`catch` that only logs a warning on failure — a GitHub API hiccup shouldn't turn a successful `POST` into a failed one, and the next scheduled daily crawl is still a fallback if the dispatch never lands (see "What happens if the dispatch fails" below).

## `ProductUpserter`: sharing the actual upsert logic

The daily crawl, the re-crawl of existing user-added products, and this one-off crawl all need to do the same thing with a scraped page: find-or-create the `Product` row, write a new `PriceSnapshot`, run restock/fake-discount detection, and notify Telegram subscribers. That logic used to be a local function inside the daily crawl's `Program.cs` — it's now `ProductUpserter.UpsertAsync`, a static method all three call sites share, so this feature didn't need to duplicate (and risk drifting from) the existing detection/notification logic.

## What happens if the dispatch fails

Two independent safety nets, not one:

- The `try`/`catch` around the dispatch call in `CreateProduct` means a GitHub API failure (rate limit, transient error, a misconfigured `GITHUB_DISPATCH_TOKEN`) doesn't fail the `POST` itself — the product is still saved and still trackable, just not filled in yet.
- Even if the dispatch silently never fires, the product is still a `UserAdded`, `IsActive` row, which the daily crawl already re-scrapes every run regardless of age (see `scheduled-crawl.md`) — so worst case, it catches up within 24 hours exactly like before this feature existed.

## A stale-deployment gotcha this feature actually ran into

See engineering log #14 — the first real test of this feature through the live site failed silently (bare row, zero matching Actions runs) not because of a bug in the feature itself, but because Back4app was still running the build from before the commit that added it. Worth knowing: if this ever seems broken again, check that the Actions tab shows *any* `crawl-product` run at all before debugging the workflow itself — zero runs means the dispatch never reached GitHub, which on this project has usually meant a stale deployment, not a code bug.

## Frontend behavior

`ProductDetailPage` polls every 4 seconds (capped at 5 minutes) while `lastCrawledAt` is null, and shows `CrawlProgressBar` — an elapsed-time estimate against a typical run (not a real step tracker; nothing reports actual progress from inside the GitHub Actions job), capped short of 100% until the crawl actually reports back. `AddProductForm` navigates straight to the new product's own page on submit, rather than leaving the user on the list page where a freshly-added, not-yet-crawled product would otherwise sort to the bottom under the default "last crawled" sort.

## Verifying it end-to-end

Confirmed by posting directly to the live API and watching a real `Crawl Product` workflow run appear in the Actions tab within seconds (not a manual/simulated run), and by submitting through the actual "track it" form and watching the browser navigate to the new product's page and the progress bar count down while `performance` entries showed a real `GET /api/products/{id}` request landing every ~4 seconds until the data arrived.
