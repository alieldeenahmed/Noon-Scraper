# Noon Scraper — Frontend

A Vite + React + TypeScript UI for the [backend API](../Backend/README.md): browse tracked products, submit a new one, see price history and fake-discount/restock flags, run an on-demand cross-merchant check, and subscribe to Telegram notifications.

**Live:** [noon-scraper-phi.vercel.app](https://noon-scraper-phi.vercel.app) — points at whichever Back4app URL is current (see `../Backend/docs/hosting.md`); if the site loads but no products show up, that's almost certainly why.

## Stack

- **Vite** + **React 19** + **TypeScript**
- **Tailwind CSS v4** (via `@tailwindcss/vite`, no separate PostCSS config needed)
- **react-router** for the two routes: the product list (`/`) and a product's detail page (`/products/:id`)

No data-fetching library — the API surface is small enough that plain `fetch` + `useState`/`useEffect` in `src/api/client.ts` covers it without adding a dependency.

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
│   ├── types.ts      # mirrors Backend/NoonScraper.Api/Dtos field-for-field
│   └── client.ts      # typed fetch wrapper, one function per endpoint
├── components/         # Layout, ProductRow, AddProductForm, PriceHistoryLog,
│                        # CheckNowPanel, NotifyMeButton, CrawlProgressBar
├── pages/
│   ├── ProductListPage.tsx
│   └── ProductDetailPage.tsx
└── lib/format.ts        # category labels, currency (EGP) and date formatting
```

`api/types.ts` is a manual mirror of the backend's DTOs rather than a generated/shared type — ASP.NET Core serializes to camelCase with string enums by default (`Program.cs`'s `JsonStringEnumConverter`), so the two line up directly without a mapping layer. If a DTO's shape changes on the backend, this file needs a matching edit.

## Telegram "Notify me"

`NotifyMeButton` only renders once `VITE_TELEGRAM_BOT_USERNAME` is set in `.env` — see [`Backend/docs/telegram-notifications.md`](../Backend/docs/telegram-notifications.md) for why subscribing has to go through a Telegram deep link rather than a form on this page.

## A local-dev gotcha worth knowing about

Windows' Smart App Control blocks Vite's native `rolldown` binding the same way it blocks freshly-compiled .NET binaries elsewhere in this project (see `Backend/docs/engineering-log.md` #3) — running `npm run dev` natively on Windows can fail with "Application Control policy has blocked this file." If that happens, run it from WSL2 instead, the same workaround already in use for the backend.
