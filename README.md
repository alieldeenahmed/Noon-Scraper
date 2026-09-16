# Noon Scraper

A full-stack data platform that scrapes live product listings from [noon.com](https://www.noon.com) — the largest e-commerce platform in the MENA region — normalizes and persists them, then runs rule-based analysis over the resulting price history to surface restocks and discounts that don't hold up against a product's own past prices. An ASP.NET Core API exposes that data to a React frontend, a Telegram bot pushes alerts when something changes, and a GitHub Actions pipeline handles every part of the pipeline that needs a real, stealth-configured browser — because the API's own hosting has no browser available to it at all.

This is not a script that dumps scraped rows into a table. It's a system with a normalized relational schema, a rule engine over time-series price data, an on-demand cross-merchant comparison feature that scrapes live rather than reading a cache, and a deliberate infrastructure split (API vs. browser-capable background jobs) driven by a real hosting constraint. Details below are verified against the actual code, not aspirational.

**Live demo:** [noon-scraper-phi.vercel.app](https://noon-scraper-phi.vercel.app) · **API:** the current URL is tracked in [`Backend/README.md`](Backend/README.md) (it changes — see [`Backend/docs/hosting.md`](Backend/docs/hosting.md) for why, and don't be surprised if it's stale)

![.NET 9](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white) ![React](https://img.shields.io/badge/React-19-61DAFB?logo=react&logoColor=black) ![TypeScript](https://img.shields.io/badge/TypeScript-3178C6?logo=typescript&logoColor=white) ![PostgreSQL](https://img.shields.io/badge/PostgreSQL-Neon-4169E1?logo=postgresql&logoColor=white) ![Playwright](https://img.shields.io/badge/Playwright-2EAD33?logo=playwright&logoColor=white) ![MIT License](https://img.shields.io/badge/license-MIT-green)

## Overview

The system tracks two kinds of products: five seeded category pages (Mobiles, Laptops, Skin Care, Hair Care, Personal Care) crawled on a daily schedule, and any product a user submits by URL, crawled immediately on submit and then kept up to date by the same daily job. Every crawl writes a new `PriceSnapshot` rather than overwriting the last one, so each product accumulates real history — that history is what the restock and fake-discount rules run against, and what the frontend renders as a price log.

Two things make this more than "scrape a page, show the data": the analysis is genuinely rule-based over stored history (not a snapshot-in-isolation heuristic), and the API itself never touches a browser — every scrape, scheduled or on-demand, is delegated to a GitHub Actions job, because the free hosting tier the API runs on has no headful-Chrome capability. That split is the main architectural decision in this codebase, and it shows up in almost every backend design choice below.

## Key Features

**Data Collection**
- Scheduled daily crawl of five product categories, plus a re-crawl of every user-submitted product on the same schedule
- Immediate, on-demand crawl the moment a user submits a new product URL, instead of waiting for the next scheduled pass
- Stealth-configured, real (headful) Chromium via Playwright — the target site hard-blocks headless Chrome at the network layer, documented in the engineering log
- Product identity normalized against Noon's per-session tracking query strings, so the same product isn't recorded as a new row every crawl

**Price Intelligence**
- Fake-discount detection: flags a claimed discount when the "before" price was itself an inflated recent spike over the product's historical low, rather than trusting the advertised percentage
- Restock detection: recorded the moment a tracked product's most recent snapshot flips from out-of-stock to in-stock
- Both are plain threshold checks against stored `PriceSnapshot` history — no ML, and every decision is traceable to the specific snapshot that triggered it

**Cross-Merchant Comparison**
- On-demand "check now" scrape that finds every seller currently offering a tracked product and returns them sorted by price
- Runs as a real live scrape (not a cached read) via the same GitHub Actions pipeline as the daily crawl, polled to completion by the client

**Notifications**
- Telegram bot subscription via a deep link (`/start <productId>`), so Telegram — not a form — supplies the chat ID
- Alerts sent on a genuine price drop or restock, with a baseline price captured at subscribe time so the first crawl after subscribing can't misfire
- `/stop` unsubscribes a chat from every product at once

**Backend**
- Three-project solution separating shared data access, the API, and the Playwright-driving crawler, so the API has zero browser dependency
- A `ProductUpserter` shared by the daily crawl, the on-demand crawl, and user-added re-crawls, so restock/discount/notification logic lives in exactly one place

**Frontend**
- Product list with search, category filters, and sortable columns (price, discount, last crawled)
- A product page that live-polls and fills in automatically while a freshly-submitted product is still being crawled, with an elapsed-time progress estimate
- Price history rendered as a chronological log, plus a dedicated cross-merchant comparison panel

## Architecture

```mermaid
flowchart LR
    User -->|browses / submits a URL| FE["React frontend<br/>(Vercel)"]
    FE -->|REST| API["ASP.NET Core API<br/>(Back4app)"]
    API -->|repository_dispatch| GHA["GitHub Actions job<br/>Playwright + headful Chrome"]
    Cron["Daily cron<br/>03:00 UTC"] -->|scheduled| GHA
    GHA -->|scrapes| Noon[noon.com]
    GHA -->|writes PriceSnapshots,<br/>runs detection rules| DB[("PostgreSQL<br/>(Neon)")]
    API -->|reads| DB
    GHA -->|price drop / restock| TG[Telegram Bot API]
    TG -->|delivers alert| Subscriber
    Subscriber -->|/start productId, /stop| TG
    TG -->|webhook| API
```

The boundary that matters here is the one between the API and the crawler. `NoonScraper.Api` has no reference to Playwright at all — it's a plain, Chrome-free ASP.NET Core app, which is what let it deploy to a free hosting tier with no browser support. Every action that needs a real browser (the daily crawl, an on-demand cross-merchant check, an immediate crawl on submit) is instead handed to `NoonScraper.Crawler`, invoked as a GitHub Actions job — triggered either by the daily cron schedule or by a `repository_dispatch` event the API fires via `GitHubDispatchService`. The API writes a placeholder row (a bare product, or a `CheckNowRequest` with `Status = Pending`) and returns immediately; the frontend polls a read endpoint until the GitHub Actions job has written the real result. `Backend/docs/check-now.md` and `crawl-on-submit.md` cover this in more depth, and `engineering-log.md` #11 explains why it wasn't built this way from the start.

## Tech Stack

| Layer | Technology | Purpose |
|---|---|---|
| Backend | ASP.NET Core (.NET 9) | REST API — read endpoints, product submission, webhook receiver |
| Frontend | React 19 + TypeScript + Vite | Single-page UI, two routes (list, detail) |
| Styling | Tailwind CSS v4 | Utility-first styling, no separate PostCSS config |
| Database | PostgreSQL (Neon) | Persists products, price history, subscriptions, flags |
| ORM | EF Core + Npgsql | Schema, migrations, querying |
| Scraping | Playwright (.NET), headful Chromium | Category and product-page scraping, cross-merchant offer scraping |
| Automation | GitHub Actions | Scheduled daily crawl; on-demand crawl and cross-merchant check via `repository_dispatch` |
| Notifications | Telegram Bot API | Webhook-based subscribe/unsubscribe, outbound alerts |
| Linting | oxlint | Frontend lint (`npm run lint`) |
| Hosting | Back4app (API, Docker) · Vercel (frontend) | Both free, card-free tiers |

No automated test suite exists in this repository (backend or frontend) — see [Testing](#testing) below.

## Backend Architecture

The backend is three .NET projects with one clear separation: **`NoonScraper.Data`** holds the EF Core models, `AppDbContext`, and the two pieces of pure logic that need no browser and no web framework — `PriceHistoryAnalyzer` (restock/fake-discount rules) and `UrlNormalizer`. **`NoonScraper.Api`** is the ASP.NET Core Web API: two controllers (`ProductsController`, `TelegramController`), two thin services (`GitHubDispatchService`, `TelegramService`), and no Playwright reference anywhere in the project. **`NoonScraper.Crawler`** is a console app that references both `NoonScraper.Data` and Playwright, and is invoked three different ways: with no arguments (the daily crawl), `check-now <requestId>`, or `crawl-product <productId>` — all three converge on the shared `ProductUpserter.UpsertAsync`.

Dependency injection is plain ASP.NET Core built-ins (`AddDbContext`, `AddHttpClient<T>`, constructor injection via primary constructors) — no additional DI container. Configuration reads through `IConfiguration` with an explicit fallback pattern repeated across `Program.cs`, `GitHubDispatchService`, and `TelegramService`: try the standard nested key first (`ConnectionStrings:DefaultConnection`, `Telegram:BotToken`), then fall back to a flat environment variable name (`DATABASE`, `TELEGRAM_BOT_TOKEN`), because Back4app's environment-variable UI rejects the double-underscore naming .NET expects for nested keys.

Validation is minimal and targeted rather than a generic framework: `ProductsController.CreateProduct` checks the submitted URL is an absolute `noon.com` link and rejects duplicates by normalized URL, returning `400`/`409` accordingly; everything else trusts route-bound IDs and returns `404` via an existence check. Error handling follows a "best-effort, don't fail the request" pattern in exactly the places where that's the honest tradeoff: dispatching a GitHub Actions run is wrapped in try/catch and only logs a warning on failure, because the product/request row is already saved either way and a GitHub API hiccup shouldn't turn a successful write into a failed response.

CORS is environment-aware rather than a single hardcoded origin: in `Development`, any `localhost` origin is trusted regardless of port (Vite's dev server doesn't always land on the same port), while outside `Development` only the deployed Vercel origin is allowed.

## Data & Database Design

```mermaid
erDiagram
    Product ||--o{ PriceSnapshot : "has history"
    Product ||--o{ NotificationSubscription : "subscribed by"
    Product ||--o{ DiscountFlag : "flagged"
    Product ||--o{ RestockEvent : "restocked"
    Product ||--o{ CheckNowRequest : "checked"
    PriceSnapshot ||--o| DiscountFlag : "triggers"
    PriceSnapshot ||--o| RestockEvent : "triggers"

    Product {
        int Id
        string Url
        string NoonProductId
        string Name
        string MerchantName
        Category Category
        decimal Rating
        ProductSource Source
        bool IsActive
    }
    PriceSnapshot {
        int Id
        int ProductId
        decimal Price
        bool Stock
        decimal DiscountPercent
        DateTimeOffset CrawledAt
    }
    DiscountFlag {
        int Id
        int ProductId
        int TriggeringSnapshotId
        decimal PriorHighPrice
        decimal DiscountedPrice
        decimal DiscountPercent
    }
    RestockEvent {
        int Id
        int ProductId
        int TriggeringSnapshotId
    }
    NotificationSubscription {
        int Id
        int ProductId
        long TelegramChatId
        decimal LastNotifiedPrice
    }
    CheckNowRequest {
        int Id
        int ProductId
        CheckNowStatus Status
        string ResultJson
    }
```

`Product.Url` carries a unique index — the enforcement point for the identity problem described in the Scraping Pipeline section below. `PriceSnapshot` is append-only: a crawl never updates a prior snapshot, it inserts a new one, which is what makes historical comparisons (and the price-history log in the UI) possible; it's indexed on `(ProductId, CrawledAt)` for that access pattern. `DiscountFlag` and `RestockEvent` both store a foreign key to the specific `PriceSnapshot` that triggered them, not just a timestamp, so a flag is always traceable back to the exact data that caused it. `NotificationSubscription` has a unique composite index on `(ProductId, TelegramChatId)` to prevent duplicate subscriptions, and every child table cascades on `Product` delete. The schema evolved across three migrations (`InitialCreate`, `AddCheckNowRequest`, `AddRestockEvent`) rather than being designed monolithically upfront.

## Scraping Pipeline

```
noon.com
  ↓  Playwright + headful Chromium, stealth init script
CategoryScraper (listing pages) / ProductPageScraper (detail pages)
  ↓  extraction
CategoryScraper reads data-qa attributes off listing tiles;
ProductPageScraper parses the page's schema.org JSON-LD block instead of the DOM
  ↓  normalization
UrlNormalizer strips tracking query params → stable product identity
  ↓  validation
ProductUpserter checks for an existing Product by normalized URL (upsert, not blind insert)
  ↓  persistence
New PriceSnapshot row + PriceHistoryAnalyzer runs restock/fake-discount checks against prior snapshots
  ↓  API
ASP.NET Core reads the resulting rows
  ↓  frontend
React polls/fetches and renders price history, flags, and restock events
```

Two extraction strategies are used deliberately, not interchangeably: category listing pages expose stable `data-qa` attributes on each product tile, so `CategoryScraper` reads those directly. The product **detail** page has no such attributes for name or rating, and the merchant name only exists in a hydrated DOM behind build-hashed CSS class names that could change on any frontend redeploy — so `ProductPageScraper` instead parses the page's embedded `schema.org` `Product` JSON-LD block, which gives clean name, SKU, price, discount, rating, seller, and real stock-availability data in one structured object. `OfferScraper` (used only for the on-demand cross-merchant check) follows a third pattern: it reads the single default offer straight from that same JSON-LD when a product has one seller, and only interacts with the DOM to expand and read the "other sellers" panel when one exists.

Headless Chrome is not an option here — the target site returns a hard connection-layer failure for headless requests, confirmed and documented in the engineering log, so every environment that runs the crawler (local WSL2, GitHub Actions) launches a real, visible Chromium instance under a virtual display (`xvfb-run` in CI) rather than true headless mode. A stealth init script (in `StealthBrowser`) overrides `navigator.webdriver`, fakes a `window.chrome.runtime` object, and populates `navigator.plugins`/`languages` to avoid the cheapest static automation fingerprints — this is explicitly documented as defeating static detection, not behavioral analysis.

Failure handling is per-item, not per-run: `CategoryScraper` failures for one category are caught and logged, and the loop continues to the next category rather than aborting the whole crawl; a user-added product that fails to scrape is skipped for that pass rather than corrupting the run. The on-demand `crawl-product` and `check-now` CLI modes each wrap their single scrape in a try/catch that records `Failed`/an error message rather than leaving the request row stuck in `Pending` forever.

## API

No authentication or authorization exists anywhere in this API — every endpoint is open. Routes are grouped by feature below rather than listed exhaustively; see `Backend/README.md` for the full reference table.

**Products**
- `GET /api/products` — list every tracked product, with each one's latest price/stock/discount already joined in; accepts an optional `?category=` filter
- `GET /api/products/{id}` — full detail for one product, including the same latest-snapshot fields
- `POST /api/products` — submit a `{ "url": "..." }` to track; validates it's a `noon.com` link, normalizes it, rejects duplicates with `409`, inserts a bare record, and best-effort dispatches an immediate crawl
- `GET /api/products/{id}/history` — every `PriceSnapshot` for a product, oldest first
- `GET /api/products/{id}/discount-flags` — every fake-discount flag raised for a product, most recent first
- `GET /api/products/{id}/restocks` — every restock event for a product, most recent first

**Cross-merchant check**
- `POST /api/products/{id}/check-now` — creates a pending check request and dispatches a live scrape via GitHub Actions; returns `202` with a request ID immediately, since there's nothing to return synchronously
- `GET /api/products/{id}/check-now/{requestId}` — poll for that request's result; once `Completed`, returns every seller's offer sorted lowest-price-first

**Telegram**
- `POST /api/telegram/webhook` — receives Telegram's webhook updates, validated against a shared secret header; handles `/start <productId>` (subscribe) and `/stop` (unsubscribe from everything). Not called by the frontend directly — Telegram calls it.

## Frontend

The frontend is two routes (`/` and `/products/:id`) under one `Layout`, with no external data-fetching or state-management library — `src/api/client.ts` is a small typed `fetch` wrapper, one function per endpoint, and each page manages its own state with `useState`/`useEffect`. `src/api/types.ts` manually mirrors the backend's DTOs field-for-field rather than being generated, which is a real coupling point noted directly in the codebase: a backend DTO change means a matching manual edit here.

`ProductListPage` fetches the product list, then filters (search, category) and sorts (last crawled / price / discount) entirely client-side over that one response — there's no server-side search or pagination. `ProductDetailPage` is the more involved of the two: on top of rendering price history, restocks, and discount flags, it detects a product with no crawl data yet and polls `GET /api/products/{id}` every four seconds (capped at five minutes) until `lastCrawledAt` is populated, showing `CrawlProgressBar` — an elapsed-time estimate against a typical run, not a real step tracker, since nothing reports granular progress from inside the GitHub Actions job. `CheckNowPanel` follows the same polling pattern against the check-now endpoints. `NotifyMeButton` links out to `https://t.me/<bot>?start=<productId>` rather than collecting any input itself, since Telegram — not this frontend — is what supplies the chat ID.

## Testing

There is no automated test suite in this repository — no backend test project (no `*.Tests.csproj` exists in the solution) and no frontend test files or test runner configured in `package.json`. Verification described throughout the docs (`engineering-log.md`, `check-now.md`, `crawl-on-submit.md`) was done manually against the live system: real product URLs, real database queries, and watching real GitHub Actions runs complete. This is a genuine gap rather than an oversight worth glossing over.

## CI/CD

There is no build/test/lint pipeline that runs on push or pull request. The three GitHub Actions workflows in this repository are compute infrastructure for scraping, not code-quality gates:

- **`daily-crawl.yml`** — runs on a `0 3 * * *` UTC cron schedule (also accepts manual `workflow_dispatch`); builds the crawler, installs Chrome + Playwright's system dependencies, and runs the full category + user-added-product crawl under `xvfb-run`
- **`check-now.yml`** — triggered by a `repository_dispatch` event from the API; runs one on-demand cross-merchant check
- **`crawl-product.yml`** — triggered the same way; crawls one freshly-submitted product immediately

All three share the same runner setup (`ubuntu-24.04`, pinned rather than `ubuntu-latest`, after hitting a Playwright compatibility break on a newer Ubuntu release locally) and now cache NuGet packages and the downloaded Playwright browser build across runs (`actions/cache`), skipping the ~150MB Chrome download entirely once that cache is warm — added specifically because every run was rebuilding from nothing.

## Project Structure

```
Noon-Scraper/
├── .github/workflows/          # daily-crawl, check-now, crawl-product
├── Backend/
│   ├── NoonScraper.Data/       # EF Core models, AppDbContext, migrations,
│   │                           # PriceHistoryAnalyzer, UrlNormalizer
│   ├── NoonScraper.Api/        # Controllers, Dtos, Services (no Playwright)
│   ├── NoonScraper.Crawler/    # Playwright scrapers, StealthBrowser,
│   │                           # ProductUpserter, TelegramNotifier
│   ├── docs/                   # hosting, telegram, check-now, engineering log
│   └── Dockerfile
└── Frontend/
    ├── src/api/                # typed fetch client + manually-mirrored DTOs
    ├── src/components/         # AddProductForm, CheckNowPanel, NotifyMeButton, ...
    └── src/pages/               # ProductListPage, ProductDetailPage
```

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Node.js (for the frontend)
- A [Neon](https://neon.tech) PostgreSQL project (or any reachable Postgres instance)
- To actually run the crawler (not just the API): a Linux environment with a display — WSL2 with Ubuntu 24.04 is what this project uses locally, since headless Chrome doesn't work against this target (see the engineering log)

### Backend

```bash
cd Backend/NoonScraper.Api    # and again for NoonScraper.Crawler
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<your Postgres connection string>"

cd ../NoonScraper.Data
dotnet ef database update --startup-project ../NoonScraper.Api
```

Telegram notifications are optional — without `Telegram:BotToken` / `Telegram:WebhookSecret` set via user secrets, the app runs fine and notifications are silently skipped.

```bash
# Run the API
cd Backend/NoonScraper.Api
dotnet run

# Run the crawler (needs Chrome + Playwright's system deps installed once:
# dotnet run -- install chrome && dotnet run -- install-deps)
cd Backend/NoonScraper.Crawler
dotnet run
```

### Frontend

```bash
cd Frontend
npm install
```

Create `Frontend/.env`:

```bash
VITE_API_BASE_URL=http://localhost:5176
VITE_TELEGRAM_BOT_USERNAME=
```

```bash
npm run dev
```

See [`Backend/README.md`](Backend/README.md) and [`Frontend/README.md`](Frontend/README.md) for the full setup — including the `DATABASE` GitHub Actions repository secret the workflows need, and the API's own `GitHubDispatch:Token` config value (a fine-grained GitHub PAT) — both required for the on-demand/scheduled crawl features to work end-to-end rather than just running the API against an empty database.

## Engineering Decisions

- **Why the API never launches a browser:** it originally did — `check-now` first shipped as a synchronous in-process scrape. That meant the API needed a headful-Chrome-capable runtime, which ruled out nearly every free hosting tier. Moving all scraping into GitHub-Actions-triggered jobs (`repository_dispatch`) made the API a plain, Chrome-free ASP.NET Core app, which is what made free hosting possible at all (`engineering-log.md` #10–#12).
- **Why headful Chrome under `xvfb-run` instead of headless:** the target site returns a hard connection-layer failure for headless requests specifically — not a JS challenge, something at the TLS/HTTP2 fingerprint level. Headful Chrome under a virtual display has the same network fingerprint as a real browser, so it works where true headless doesn't.
- **Why product detail scraping reads a JSON-LD block instead of the DOM:** the detail page's DOM has no stable attributes for name/rating and uses build-hashed class names for the seller field that could change on any frontend redeploy. The page's own embedded `schema.org` structured data is more stable and happened to also expose real stock availability, which the DOM didn't expose at all.
- **How duplicate products are prevented:** Noon appends a per-session tracking query string to every product URL. Without normalizing it away, the same real product was being inserted as a new row on every crawl (a real bug caught during testing — 10 duplicate rows for one item). `UrlNormalizer.Normalize()` strips to scheme+host+path before any write or comparison, shared between the crawler and the API's own duplicate check.
- **How price history is stored:** append-only `PriceSnapshot` rows rather than mutating a single "current price" field, specifically because both detection rules and the frontend's price-history log need the full sequence, not just the latest value.
- **How scraping failures are handled:** per-item try/catch during the category crawl loop (one category failing doesn't abort the run), and a dedicated `Failed` status with an error message for on-demand requests, rather than either crashing the whole job or leaving a request silently stuck.
- **Why check-now is async (poll-based) instead of a synchronous response:** there's nothing to return until the GitHub Actions job actually finishes the scrape, and a synchronous HTTP handler can't sit open for the minutes that takes — so the API returns `202` with an ID immediately and the client polls a second endpoint.

## What I deliberately didn't claim

No authentication, no automated tests, and no CI validation pipeline exist in this codebase — they're not glossed over above, and adding them (particularly integration tests around `PriceHistoryAnalyzer` and the upsert/normalization path) would be the most valuable next step for this project's engineering maturity.

## License

Licensed under the [MIT License](LICENSE).
