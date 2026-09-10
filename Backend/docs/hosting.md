# Hosting the API

The API is deployed on **[Back4app Containers](https://www.back4app.com/)**, building `Backend/Dockerfile` straight from this GitHub repo on every push. This doc covers the practical setup; see the engineering log for the full story of how I landed here.

## Why Back4app

I don't have a credit card, and by the time I went looking, that ruled out almost every option I tried: Google Cloud Run and Fly.io both require a billing account/card just to create an app, and Render's free Web Service tier — despite its own docs saying otherwise — asked for a card during signup in practice. Koyeb, Railway, Clever Cloud, and Scalingo all turned out to offer a card-free *trial* rather than a permanent free tier. Back4app Containers was the one platform I found whose free tier (0.25 CPU, 256MB RAM, 100GB transfer/month) is genuinely ongoing with no card, and it deploys arbitrary Docker images from GitHub — which is exactly what `Backend/Dockerfile` already was, once the API stopped needing Chrome (see `check-now.md`).

## Environment variables

Set these under the container's **Environment Variables** (runtime, not build-time):

| Key | Value |
|---|---|
| `DATABASE` | The Neon connection string, in Npgsql's key-value format: `Host=...;Database=...;Username=...;Password=...;SSL Mode=Require` — **not** Neon's raw `postgresql://...` URI (see `database-setup.md`'s connection-string gotcha). |
| `GITHUB_DISPATCH_TOKEN` | The fine-grained GitHub PAT (`Actions: write`) used to trigger `check-now.yml` — see `check-now.md`. Without it, `check-now` returns a `502` ("Failed to trigger the check"). |
| `TELEGRAM_BOT_TOKEN` | Optional — see `telegram-notifications.md`. Without it, notifications are silently skipped. |
| `TELEGRAM_WEBHOOK_SECRET` | Optional, same doc — only needed once the real Telegram webhook is registered. |

All four are read under these flat names specifically because Back4app's environment-variable UI rejects the double-underscore/colon naming (`ConnectionStrings__DefaultConnection`, `GitHubDispatch:Token`, `Telegram:BotToken`) that .NET normally expects for nested config — see each service's own fallback (`Program.cs`, `GitHubDispatchService.cs`, `TelegramService.cs`).

**Every one of these has to be re-entered whenever the app gets recreated** (see the URL-stability gotcha below) — they don't carry over automatically except, apparently, `DATABASE`, which survived a recreation once for reasons I don't fully understand. Don't assume any of them are still set after a recreation; check.

## Gotchas hit getting this working

- **`SSL Mode=Require` typo'd as `Requir`** (a truncated paste) fails with a genuinely confusing Npgsql error (`Requested value 'Requir' was not found`) that only makes sense once you know to look at exactly that keyword.
- **No visible runtime log viewer** on the free tier at the time I set this up — only build/deploy logs. Diagnosing a bare `500` with an empty response body required temporarily adding an exception-detail middleware directly to the API (writing `ex.ToString()` into the response) rather than relying on any dashboard log view. That middleware was removed once the deployment was confirmed working — leaving it in would leak stack traces and config to anyone hitting a broken endpoint.
- **Auto-deploy on push isn't instant, and occasionally seems to miss a push entirely** — Back4app is connected via a GitHub App (not a classic webhook, so it doesn't show under the repo's Settings → Webhooks), and one push during setup produced no new deployment at all until a later, unrelated push finally triggered one. If a push doesn't seem to be landing, a trivial follow-up commit is a reasonable way to re-trigger it — there's no manual "redeploy" button on the free tier to fall back on.
- **The container can go silently unreachable between deploys, and it doesn't wake back up on its own.** More than once, a deployment that worked right after going live later started returning a bare CloudFront-level 404 (no `server: Kestrel` header at all, meaning the request never reached the app) with the dashboard still showing "Deployed" the whole time. Hitting it repeatedly over ~90 seconds never woke it — this isn't a per-request cold start, whatever it is.
- **The only recovery available on the free tier is deleting and recreating the container app — which assigns a brand-new random subdomain.** This has happened three times now: `0v70vtd1` → `v4uc8dwj` → `dwfi2d0a`. There's no way (found so far) to keep the same URL through a recreation. Worth knowing before pointing anything external (a frontend's `.env`, a Telegram webhook registration, a resume link) at the current URL — it may not be the URL a month from now, or even a day from now.

## Recovery checklist, every time the URL changes

Everything below has an independent, easy-to-forget reference to the current backend URL:

1. Re-check/re-enter all four environment variables (see above) on the new container.
2. Update the **`Live:`** link at the top of this README.
3. Update `VITE_API_BASE_URL` in the deployed frontend's env vars (Vercel project settings) and trigger a redeploy — this is baked in at build time, not read at runtime, so editing the env var alone does nothing until the next build.
4. Re-register the Telegram webhook (`setWebhook`, see `telegram-notifications.md`) — it's pointing at whatever URL was current when it was last registered, and silently stops delivering (not erroring loudly) once that URL dies.
5. Add the new URL to CORS on the API side is **not** needed — CORS is about the frontend's origin, which doesn't change here.

## What's deliberately not here

`render.yaml` (from an earlier, abandoned attempt to deploy on Render) has been removed — Back4app doesn't use it, and keeping a dead deployment manifest around would be misleading.
