# Scraper fixtures

Saved noon.com markup used by the scraper tests (`CategoryScraperTests`, `ProductPageScraperTests`, `OfferScraperTests`). The tests run the real scrapers in a real browser with the network cut off — every request is answered with HTML built from these files — so the actual selectors are exercised, not a mock of them.

Captured on 2026-09-19 from `noon.com/egypt-en`, then trimmed to the structure the scrapers read (images, SVG, inline styles, unrelated JSON-LD properties removed). Hashed CSS-module class names (`_amount_1o2w0_59`, …) are kept exactly as they appeared.

| File | What it is |
|---|---|
| `category-page.html` | Three **unmodified** tiles from the Mobiles listing (two discounted, one not) |
| `category-tile.template.html` + `*-block.html` | One real tile with its variable parts (href, name, price, discount, rating, `data-qa` names) replaced by tokens, so tests can build edge cases from real structure |
| `product-anker.jsonld.json`, `product-lenovo.jsonld.json` | Real `schema.org` `Product` blocks, trimmed to the fields the scraper reads plus identity fields |
| `breadcrumb.jsonld.json` | A real non-Product block that sits alongside the Product one on the page |
| `price-now.html` | The real `data-qa="div-price-now"` element the scraper waits for |
| `offer-cards.html` | Six real seller cards from a product with a dozen sellers: the "Selected" card, one with a discount badge, one with "No ratings yet.", one with a decimal price |
| `offers-trigger.html` | The "More offers from other sellers" trigger, as it appears on the page |

## What is derived, not captured

- **The search-grid tile template** (`plp-product-box-name` / `plp-product-box-price`). `CategoryScraper` handles two Noon templates, but every tile on the live Mobiles page used the carousel one, so the grid variant is the real tile with those two attribute names swapped.
- **Discount and rating text variants** (Arabic badge text, "16% OFF", no rating): the real badge markup with different text inside.
- **The other-sellers panel is injected on click** by a small script in the test page. On the real site the cards only exist in the DOM after the trigger is clicked; the fixture reproduces that behaviour around the real card markup.
- **JSON-LD edge cases** (a `priceSpecification` for discounts, `OutOfStock`, `offers` as an array, a missing seller or rating) are the real blocks edited in the test.

## What these tests can't tell you

They are static snapshots. They catch a change to the *scraper* that breaks parsing; they cannot notice that Noon changed its *markup* — that only shows up against the live site. When a scrape starts failing in production, capture fresh markup, update the fixture, and the tests become the regression check for the fix.
