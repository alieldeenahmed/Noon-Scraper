# Hosting the API

The API is deployed on **[Back4app Containers](https://www.back4app.com/)**, building `Backend/Dockerfile` straight from this GitHub repo on every push. This doc covers the practical setup; see the engineering log for the full story of how I landed here.

## Why Back4app

I don't have a credit card, and by the time I went looking, that ruled out almost every option I tried: Google Cloud Run and Fly.io both require a billing account/card just to create an app, and Render's free Web Service tier — despite its own docs saying otherwise — asked for a card during signup in practice. Koyeb, Railway, Clever Cloud, and Scalingo all turned out to offer a card-free *trial* rather than a permanent free tier. Back4app Containers was the one platform I found whose free tier (0.25 CPU, 256MB RAM, 100GB transfer/month) is genuinely ongoing with no card, and it deploys arbitrary Docker images from GitHub — which is exactly what `Backend/Dockerfile` already was, once the API stopped needing Chrome (see `check-now.md`).

## Environment variables

Set these under the container's **Environment Variables** (runtime, not build-time):

| Key | Value |
|---|---|
| `DATABASE` | The Neon connection string, in Npgsql's key-value format: `Host=...;Database=...;Username=...;Password=...;SSL Mode=Require` — **not** Neon's raw `postgresql://...` URI (see `database-setup.md`'s connection-string gotcha). |

The API reads this key specifically because Back4app's environment-variable UI rejects the double-underscore naming (`ConnectionStrings__DefaultConnection`) that .NET normally expects for nested config — see [`Program.cs`](../NoonScraper.Api/Program.cs) for the fallback that handles this.

## Gotchas hit getting this working

- **`SSL Mode=Require` typo'd as `Requir`** (a truncated paste) fails with a genuinely confusing Npgsql error (`Requested value 'Requir' was not found`) that only makes sense once you know to look at exactly that keyword.
- **No visible runtime log viewer** on the free tier at the time I set this up — only build/deploy logs. Diagnosing a bare `500` with an empty response body required temporarily adding an exception-detail middleware directly to the API (writing `ex.ToString()` into the response) rather than relying on any dashboard log view. That middleware was removed once the deployment was confirmed working — leaving it in would leak stack traces and config to anyone hitting a broken endpoint.
- **Auto-deploy on push isn't instant, and occasionally seems to miss a push entirely** — Back4app is connected via a GitHub App (not a classic webhook, so it doesn't show under the repo's Settings → Webhooks), and one push during setup produced no new deployment at all until a later, unrelated push finally triggered one. If a push doesn't seem to be landing, a trivial follow-up commit is a reasonable way to re-trigger it — there's no manual "redeploy" button on the free tier to fall back on.

## What's deliberately not here

`render.yaml` (from an earlier, abandoned attempt to deploy on Render) has been removed — Back4app doesn't use it, and keeping a dead deployment manifest around would be misleading.
