# Noon Scraper — Backend

An ASP.NET Core backend that tracks prices and stock on [noon.com](https://www.noon.com) (the largest e-commerce platform in the MENA region), with rule-based restock and fake-discount detection, and a live cross-merchant price comparison endpoint.

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
- **`NotificationSubscription`** — links a Telegram `chat_id` to a product (not wired up to actual notification-sending yet).
- **`DiscountFlag`** — written when the fake-discount detector fires: the inflated "before" price, when it was seen, the discounted price, and the snapshot that triggered the flag.

### Tech stack

- **.NET 9** / ASP.NET Core Web API
- **EF Core** + **Npgsql** against **PostgreSQL on Neon** (chosen specifically because it's reachable from GitHub Actions runners — a local Postgres instance or Render's free Postgres, which expires after 90 days, wouldn't work for a scheduled cloud cron job)
- **Playwright** (real Chrome, not headless — see the engineering log) for scraping
- **WSL2 (Ubuntu 24.04)** as the local dev/test environment for anything that launches a browser — required due to a Windows-specific blocker, also explained in the engineering log

## Getting started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A [Neon](https://neon.tech) Postgres project
- **For running the Crawler or the API's `check-now` endpoint specifically**: WSL2 with Ubuntu 24.04, Google Chrome, and Playwright's system dependencies installed. Plain read endpoints on the API don't need any of this — only code paths that actually launch a browser do. See `docs/database-setup.md` and the engineering log for the full setup.

### Configuration

Connection strings are never committed. Locally, each project (`NoonScraper.Api`, `NoonScraper.Crawler`) reads its `ConnectionStrings:DefaultConnection` from **.NET user secrets**:

```bash
cd NoonScraper.Api   # or NoonScraper.Crawler
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<your Neon connection string>"
```

In CI, the same key is read from an environment variable (`ConnectionStrings__DefaultConnection`) instead — the config system picks either up automatically.

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

The Crawler and the API's `check-now` endpoint both launch a real, visible (headful) Chrome instance — this only works from an environment with a display, which is why WSL2 is required locally (see the engineering log for exactly why headless doesn't work here).

## API reference

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/products` | List tracked products, optional `?category=` filter, includes each product's latest price/stock/discount |
| `GET` | `/api/products/{id}` | Full detail for one product |
| `GET` | `/api/products/{id}/history` | Full price/stock history for one product |
| `POST` | `/api/products` | Submit a URL to track (`{ "url": "..." }`) — validates it's a noon.com link, normalizes it, rejects duplicates, inserts a bare record that the next crawl fills in |
| `POST` | `/api/products/{id}/check-now` | Live, on-demand cross-merchant price check — scrapes the product's page right now (not from stored data) and returns every competing seller's price, sorted lowest first |

## Current status

**Done:** data model, category-page crawling (5 categories: Mobiles, Laptops, Skin Care, Hair Care, Personal Care), user-submitted URL tracking with detail-page scraping, restock detection, fake-discount detection, on-demand cross-merchant check-now.

**Not yet built:** Telegram notifications (the model exists, the send logic doesn't), the actual GitHub Actions cron schedule (the crawler works, but nothing runs it automatically yet), and deployment to Render.

**Explicitly out of scope for V1:** crawling Noon's full catalog (only tracked products, seeded + user-submitted), anything behind login/checkout, a seasonal "best time to buy" predictor (needs months of data this project doesn't have yet), and treating cross-merchant comparison as a continuous background feature rather than an on-demand action.

See [`docs/database-setup.md`](docs/database-setup.md) for how the database is provisioned, and [`docs/engineering-log.md`](docs/engineering-log.md) for a full account of the technical obstacles this project ran into and how each one got resolved — including the anti-bot investigation, a local Windows tooling blocker, a real data-integrity bug, and how the pricing/offer data actually gets extracted.
