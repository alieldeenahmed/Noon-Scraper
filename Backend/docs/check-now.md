# On-demand cross-merchant check ("check now")

## Why this isn't a simple synchronous endpoint

The obvious design for `POST /api/products/{id}/check-now` is to launch Chrome right there in the request handler, scrape, and return the result. That's what I originally built, and it worked — but it means the API itself needs a Chrome-capable, headful-browser-capable runtime, which rules out almost every free hosting tier (most either don't support launching a real browser at all, or only offer that on paid plans). Since the daily crawl already solved "run headful Chrome on a schedule" using GitHub Actions, the same approach works for on-demand checks too — the only thing that changes is what triggers the job.

## The flow

1. `POST /api/products/{id}/check-now` creates a `CheckNowRequest` row (`Status = Pending`) and fires a `repository_dispatch` event to GitHub (`GitHubDispatchService`, using a fine-grained PAT with `Actions: write` scope, stored in the API's own configuration — never committed).
2. That immediately returns `202 Accepted` with the new request's id — there's nothing to scrape yet, so there's nothing to return synchronously.
3. GitHub Actions receives the dispatch and runs [`check-now.yml`](../../.github/workflows/check-now.yml) — the same Ubuntu 24.04 + Chrome + Playwright-deps setup as the daily crawl, just triggered by an event instead of a cron schedule.
4. The workflow runs `NoonScraper.Crawler check-now <requestId>`, which loads that specific `CheckNowRequest`, scrapes the product's page (`OfferScraper`, same stealth `StealthBrowser` setup the daily crawl uses), and writes the result back: `Status = Completed` with `ResultJson` holding every seller's offer, or `Status = Failed` with `ErrorMessage` if the scrape didn't find anything.
5. The client polls `GET /api/products/{id}/check-now/{requestId}` until `Status` stops being `Pending`.

## Why `OfferScraper`/`OfferResult`/`StealthBrowser` live in `NoonScraper.Crawler`, not `NoonScraper.Api`

They used to live in the API (`Services/`), from when check-now scraped in-process. Once the API stopped launching a browser at all, that code had no reason to reference Playwright, so it moved into the Crawler project — the only place that actually needs it. `OfferDto` (API) and `OfferResult` (Crawler) are deliberately two separate types with an identical shape rather than a shared reference between the two projects: `CheckNowRequest.ResultJson` is just a JSON string in the database, so the API deserializes it into its own DTO without needing to reference the Crawler assembly at all.

## Verifying it end-to-end

Confirmed against a real tracked product (an Anker USB-C charger with 3 known competing sellers): `POST /check-now` returned `202` with a request id, the `check-now.yml` run appeared in the Actions tab within seconds (triggered by `repository_dispatch`, not a manual run), and polling the result endpoint went `Pending` → `Completed` with the correct three offers, sorted lowest-price-first, matching what the original in-process version had returned for the same product.
