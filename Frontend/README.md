# Noon Scraper — Frontend

A Vite + React + TypeScript UI for the [backend API](../Backend/README.md): browse tracked products, submit a new one, see price history and fake-discount/restock flags, run an on-demand cross-merchant check, and subscribe to Telegram notifications.

**Live:** [noon-scraper-phi.vercel.app](https://noon-scraper-phi.vercel.app) — points at whichever Back4app URL is current (see `../Backend/docs/hosting.md`); if the site loads but no products show up, that's almost certainly why.

## Stack

- **Vite** + **React 19** + **TypeScript**
- **Tailwind CSS v4** (via `@tailwindcss/vite`, no separate PostCSS config needed)
- **react-router** for the two routes: the product list (`/`) and a product's detail page (`/products/:id`)

No data-fetching library — the API surface is small enough that plain `fetch` + `useState`/`useEffect` in `src/api/client.ts` covers it without adding a dependency. **zod** validates every API response against the contract (below), and **Vitest** + React Testing Library run the tests.

## Getting started

Create `.env` in this folder (the repo's root `.gitignore` blocks every `.env*` file, so there's no template to copy — this is the whole thing):

```bash
VITE_API_BASE_URL=http://localhost:5176
VITE_TELEGRAM_BOT_USERNAME=
```

Then:

```bash
npm install
npm run dev
```

Needs the backend running locally too (`cd ../Backend/NoonScraper.Api && dotnet run`) — `VITE_API_BASE_URL` is what it points at. `VITE_TELEGRAM_BOT_USERNAME` is optional: leave it blank and the "Notify me" button just hides itself (see below).

## Structure

```
src/
├── api/
│   ├── schemas.ts        # the API contract, as zod schemas - the one definition
│   ├── types.ts          # TypeScript types inferred from those schemas
│   ├── client.ts         # fetch wrapper: one function per endpoint, validates each response
│   ├── client.test.ts    # request building, error statuses, contract violations
│   └── contract.test.ts  # parses the backend's pinned real-API fixtures with the schemas
├── components/           # Layout, ProductRow, AddProductForm, PriceHistoryLog, CheckNowPanel,
│                         # JobFailure, NotifyMeButton, CrawlProgressBar  (+ *.test.tsx)
├── pages/                # ProductListPage, ProductDetailPage  (+ *.test.tsx)
├── test/                 # setup, object factories, a stub for fetch
└── lib/format.ts         # category labels, currency (EGP) and date formatting
```

## The API contract

There's no code generator - a schema-first toolchain would be bigger than this app. Instead the response shapes are written once, as zod schemas in `api/schemas.ts`; the TypeScript types are inferred from them, and every response is validated against its schema when it arrives. If the backend's DTOs drift from what the UI expects, the failure is a `ContractError` ("Unexpected response from /api/...") at the boundary rather than an `undefined` several components away. Non-2xx responses become an `ApiError` carrying the status and, from the API's RFC 7807 body, its explanation (`detail`) and, on a `409`, the existing product's id.

The other half lives in the backend: `Backend/NoonScraper.Tests/ContractTests.cs` pins the JSON the real API produces (a crawl that is pending, running, failed; a failed and a completed check; the problem-details errors) into `Backend/NoonScraper.Tests/Contracts/*.json`, and `contract.test.ts` parses those same files with these schemas. Change a DTO and the backend test fails until the fixtures are regenerated (`NOON_UPDATE_CONTRACTS=1 dotnet test --filter ContractTests` - a visible diff); regenerate them and a schema that hasn't caught up fails here. It catches drift in CI; it doesn't remove the need to edit `schemas.ts` when a field is added.

## Tests

```bash
npm test
```

They render the real components against the real API client with `fetch` stubbed, so they cover how the UI reacts to what actually comes back over HTTP (statuses, problem details, unparseable bodies), not to a mocked function. Covered: submitting a product (success, 400 with the API's reason, 409 with a link to the existing product, 429, network failure, double submit); the product page waiting on a crawl (queued, crawling, filling in, a *failed* crawl with its reason/stage/workflow link, the 5-minute give-up, a dropped connection, "not found" vs an outage); the cross-merchant check (Pending -> Running -> Completed, no sellers, failure, timeout, lost connection, giving up, polling stopped on unmount); price-history rendering (first reading, up / down / steady, same-day times); the list (sort direction and reset to page 1, category filter, debounced search, paging, empty and error states, and a slow superseded response not overwriting a newer one); and the Notify link. What they don't cover: layout and styling, and a real browser talking to a real API - that's jsdom.

## The product list is server-driven

`ProductListPage` doesn't hold the whole catalog: search (debounced), category, sort key/direction, and page number are all sent to `GET /api/products`, which returns one page plus a total (`PagedResult<T>` in `api/types.ts`). The headline counts come from `GET /api/products/stats`. Changing any filter or sort resets to page 1, and a response that's been superseded by a newer query is dropped rather than rendered. A `429` from the API's rate limiter is turned into a plain-language message in `AddProductForm` and `CheckNowPanel`.

Before shipping, `npm run lint` (oxlint), `npm test` and `npm run build` (`tsc -b` then Vite) all need to pass - CI runs the same three commands.

## Telegram "Notify me"

`NotifyMeButton` only renders once `VITE_TELEGRAM_BOT_USERNAME` is set in `.env` — see [`Backend/docs/telegram-notifications.md`](../Backend/docs/telegram-notifications.md) for why subscribing has to go through a Telegram deep link rather than a form on this page.

## A local-dev gotcha worth knowing about

Windows' Smart App Control blocks Vite's native `rolldown` binding the same way it blocks freshly-compiled .NET binaries elsewhere in this project (see `Backend/docs/engineering-log.md` #3) — running `npm run dev` natively on Windows can fail with "Application Control policy has blocked this file." If that happens, run it from WSL2 instead, the same workaround already in use for the backend.
