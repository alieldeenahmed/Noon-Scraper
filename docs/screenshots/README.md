# Screenshots

The `demo-*.png` images were captured from a local run against **synthetic demo data**
(`Backend/NoonScraper.DemoData`). Every product in them is invented: names start with
"[Demo]" and nothing was scraped from noon.com to make them. They show the app's behaviour,
not real prices.

| File | What it shows |
|---|---|
| `demo-product-list.png` | The list sorted by discount, with category filters, search and counts |
| `demo-fake-discount.png` | A product whose "-25%" sale is flagged: the price spiked ~40% for a week, then "dropped" to a price still above its own recent low. The flag entry is at the bottom |
| `demo-restock.png` | A restock event recorded when a product came back in stock |
| `demo-product-list-mobile.png` | The list at phone width |

`capture.mjs` regenerates them (instructions at the top of the file).

`product-list.png`, `product-detail.png` and `product-list-mobile.png` are older captures from
a local run against real scraped noon.com data. They are kept but are no longer used by the README.
