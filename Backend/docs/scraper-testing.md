# Scraper testing strategy

A scraper has two kinds of code that fail in different ways, so I test them in different layers.

| Layer | What breaks it | How it's tested | Needs Chrome? |
|---|---|---|---|
| **Parsing** — turn text into numbers and flags | odd price formats, missing fields, malformed JSON-LD | plain unit tests on pure functions | no |
| **Extraction** — find the right elements in the page | Noon changes its markup; a selector is wrong | the real scrapers, in a real browser, against saved noon.com markup | yes |
| **Job logic** — what happens when a scrape fails, is slow, or is repeated | timeouts, blocks, one bad item, cancellation, duplicate delivery | the job classes against a scripted fake browser session and a real database | no |
| **Live site** — does any of it still match noon.com today | Noon redeploys | **not tested automatically** | — |

The last row is the honest limit. Fixtures are static snapshots. They catch *my* change to a scraper that breaks parsing; they cannot notice that Noon changed its markup. Only a run against the live site does, and that's what the scheduled crawl's failure policy is for: a broken selector shows up as a red daily run and failed categories, not as silently empty data.

## Making it testable: separating parsing from the browser

The original scrapers mixed both: locate an element, read its text, parse the price, decide stock, all against Playwright objects. The only way to test a price-format edge case was to run Chrome. I pulled the pure parts out:

- `PriceText` — reads the first number in text like `"EGP 1,299.50"`, `"16% OFF"` or `"خصم ٦٪"` (thousands commas, Arabic-Indic digits and Arabic separators), and refuses text with no number, instead of each scraper carrying its own regex. It deliberately does *not* try to understand European `1.299,50` — Noon's Egyptian pages don't use it, and guessing would be worse than failing.
- `ProductJsonLd` — takes the JSON-LD text of a page and returns a product: picks the `Product` block out of several (a breadcrumb block sits beside it), reads `offers` whether it's an object or an array, derives the discount from a pre-discount `priceSpecification`, maps `availability` to stock, and reads the seller. A missing name, SKU or offer, and a zero, negative or unparseable price, are errors rather than silently becoming a bad row; a malformed block is skipped.
- `IScrapeSession` — the jobs (`DailyCrawl`, `CrawlProductJob`, `CheckNowJob`) depend on this interface (`ScrapeCategoryAsync`, `ScrapeProductAsync`, `ScrapeOffersAsync`, `ResetAsync`), not on a browser. The real implementation wraps `StealthBrowser`; tests substitute a scripted fake.

The scrapers also throw *typed* exceptions (`ScrapeParseException`, `ScrapeNoDataException`, `ScrapeNavigationException`). The type is what decides whether a failure is worth retrying and what the user is told, so a page with no products is no longer "an exception" but "a parse failure", and a 429 is transient while a 404 isn't.

## Fixture-based extraction tests

`CategoryScraperTests`, `ProductPageScraperTests`, `OfferScraperTests` run the real scrapers in a real headless Chrome with the network cut off: Playwright answers every request with HTML assembled from saved markup in `NoonScraper.Tests/Fixtures/`. The selectors that run are the production selectors. `Fixtures/README.md` lists what is a real capture, what is trimmed, and — importantly — what is *derived* (edited from a real block to build an edge case), so nothing derived is passed off as captured.

What's covered:

- **Listing tiles:** three real, unmodified tiles field by field; both Noon tile templates; thousands separators; Arabic and English discount badges; a missing rating; tiles that aren't product links are skipped (and *reported* as skipped, with a reason); a page with no tiles is a parse failure so the crawl marks that category failed.
- **Product pages:** two real JSON-LD products; discount from a pre-discount price including the "not actually higher" boundary; stock from `availability` (in stock, out of stock, missing); `offers` as an array; missing rating / seller; tracking parameters in the URL; a malformed or non-Product block is skipped; no Product block returns "no data".
- **Other sellers:** six real seller cards read from the opened panel (the selected card, a discounted one, "No ratings yet.", a decimal price); fallback to the page's own offer when there's no panel, no cards or no seller.
- **Parsing:** `PriceText` and `ProductJsonLd` directly (`ParsingTests`) — thousands and decimal separators, Arabic digits and separators, text with no number, zero / negative / non-numeric / wrong-JSON-type prices, missing name / SKU / offers, numeric SKUs read as text, an absurdly large number not crashing.

## Do these tests fail when they should?

A test that can't fail is decoration, so I broke the code on purpose and checked. Mutations tried, each caught by the tests I expected:

- the discount selector in `CategoryScraper` (a tile's discount came back null);
- the seller-price selector in `OfferScraper`;
- the `>` in the pre-discount comparison in `ProductJsonLd` (flipped to `>=`, so "not actually higher" produced a discount).

Those three were checked when the fixture tests were first written, before the parsing was moved into `PriceText` / `ProductJsonLd`; I haven't re-run the mutations since the refactor beyond the suite passing, so treat the claim as "these tests were shown to be able to fail", not as a mutation score.

## Job-logic tests (no browser)

The failure semantics from [failure-model.md](failure-model.md) are tested against `FakeScrapeSession`, a scripted stand-in whose category / product / offer calls are delegates the test controls, plus a real PostgreSQL database. That lets a test say "the second product times out once, then succeeds" or "the third category throws a parse error" and check exactly what was written:

- a category that fails does not stop the others and turns the run red; a transient failure is retried on a *reset* page and can recover; a permanent one isn't retried;
- one unrecordable product among many is counted and skipped; **a failed database write (provoked with a trigger that rejects the insert) doesn't poison the products after it**;
- the time budget defers the remainder oldest-first;
- overlapping runs: one wins, one exits cleanly; a run that died is expired;
- cancellation keeps what was committed and reports partial counts; the exit-code policy is a table of cases;
- for dispatched jobs: claim-once (duplicate delivery does nothing), timeout, cancellation, "the request was expired while the worker ran" (result discarded), and that log lines carry the request / product / correlation ids.

## What I'd add with more time

- A **contract check against the live site**, run on a schedule, that fetches one real listing and one product page and asserts the fixtures' assumptions still hold (selectors match, JSON-LD has a `Product`). It would be a canary rather than a test: it can fail for reasons outside my control, so it shouldn't gate a merge.
- Recording fresh fixtures automatically when that canary fails.
