# Telegram notifications

## Why a webhook, and why "subscribing" needs a deep link

A Telegram bot can't message a user until that user has messaged the bot first — that's a platform rule, not a design choice. So subscribing can't just be "type your chat ID into a form"; the user has to actually talk to the bot at least once, and Telegram needs to be the one to hand us their chat ID.

The standard pattern (and what's implemented here): a "Notify me" link on a product points to `https://t.me/<BotUsername>?start=<productId>`. Clicking it opens Telegram and sends `/start <productId>` to the bot the instant the user taps Start. Telegram delivers that as an update to our bot's webhook — `POST /api/telegram/webhook`, handled by `TelegramController` — which already has the user's `chat_id` attached. No manual entry, no separate account linking step.

## The two halves, and why they live in different projects

- **Subscribe/unsubscribe** (`TelegramController`, in `NoonScraper.Api`) has to be always-on, since Telegram can deliver a webhook call at any moment — this is exactly what the API already is.
- **Actually sending a notification** (`SubscriptionNotifier`, in `NoonScraper.Data`, over a small `TelegramSender`) only makes sense at the moment a product's price or stock actually changes — and the only place that ever learns that is the crawler, right after `ProductRecorder` commits a new `PriceSnapshot`. Notifications are sent *after* the commit, and each is claimed with a conditional update before it's sent, so two overlapping crawls can't both send the same alert and a failed send doesn't advance the baseline. The same code runs for the daily crawl and the on-demand `crawl-product` run.

## When a notification fires

`NotificationSubscription.LastNotifiedPrice` is the baseline. A message goes out when, for a given subscription:

- the product's stock just flipped from out-of-stock to in-stock (reuses `PriceHistoryAnalyzer.IsRestockAsync`, the same check the restock log line uses), **or**
- the new price is lower than `LastNotifiedPrice`.

`LastNotifiedPrice` gets set the moment someone subscribes (to the product's current price, if it's been crawled at least once) specifically so the *next* crawl has something real to compare against — without that, the first ever comparison would have no baseline and could either wrongly fire or wrongly stay silent depending on how the null case were handled. If a subscription somehow still has no baseline by the time a crawl reaches it (e.g. subscribed to a product that had never been crawled yet), one gets established silently on that pass rather than firing a notification off an unknown "prior" price.

## Setup

1. **Create the bot** — message [@BotFather](https://t.me/BotFather) on Telegram, `/newbot`, pick a name and a `...bot`-suffixed username. It replies with a token.
2. **Store the token** — never commit it or paste it into an AI chat. Locally:
   ```bash
   cd NoonScraper.Api    # and again for NoonScraper.Crawler
   dotnet user-secrets set "Telegram:BotToken" "<token>"
   ```
   `daily-crawl.yml` and `crawl-product.yml` are the workflows that record readings and so send notifications (`check-now.yml` only compares sellers and never writes a `PriceSnapshot`, so it has no use for this token); GitHub Actions accepts the normal double-underscore env var name fine. Back4app doesn't, though, so the settings helper (`SettingsExtensions`) also checks a flat `TELEGRAM_BOT_TOKEN` as a fallback — same restriction as the database connection string (see `hosting.md`).
3. **Pick a webhook secret** — any random string, used only to confirm incoming webhook calls genuinely came from Telegram (checked, in constant time, against the `X-Telegram-Bot-Api-Secret-Token` header). Store it the same way, under `Telegram:WebhookSecret` / `TELEGRAM_WEBHOOK_SECRET`. This one only needs to exist on the API, not the Crawler. **Outside `Development` the webhook refuses every request (`503`) until it's set** — with no secret there's nothing to authenticate Telegram against, so it fails closed rather than accepting anyone.
4. **Register the webhook** — once the API has a real public URL, call this once:
   ```bash
   curl "https://api.telegram.org/bot<TOKEN>/setWebhook?url=https://<your-api-host>/api/telegram/webhook&secret_token=<your-chosen-secret>"
   ```
   Confirmed working end-to-end: a real `/start <productId>` via the deep link creates a real `NotificationSubscription` row through the live webhook, not just the local simulation used earlier in development.

### Debugging the webhook itself

`GET https://api.telegram.org/bot<TOKEN>/getWebhookInfo` is the fastest way to see what Telegram thinks is happening — `url`, `pending_update_count`, and `last_error_message` cover most failure modes. Two things worth knowing:

- **`last_error_message` doesn't clear on success** — it's the most recent error, not the current status. A successful delivery after a run of failures still leaves the old error message sitting there. If notifications are actually arriving (or, more directly, a real subscription shows up in the database after clicking the deep link), the webhook is working regardless of what this field says.
- **A `401 Unauthorized` here means a secret mismatch between what `setWebhook`'s `secret_token` was called with and whatever `Telegram:WebhookSecret`/`TELEGRAM_WEBHOOK_SECRET` actually holds on the running server** — in practice this came from a copy-paste mistake pasting the value into Back4app's environment-variable field. Re-generating a fresh secret and setting it in both places in the same sitting (rather than trying to compare two already-set values) is the fastest way to rule this out.

## Unsubscribing

Only text messages in *private* chats are acted on, and a chat can hold at most 25 subscriptions. There's no per-product "stop notifying me" from the frontend — the frontend has no concept of *who* is asking (no auth, no way to know a browser's Telegram chat ID). Unsubscribing happens by messaging the bot `/stop`, which removes every subscription for that chat at once. Kept deliberately simple for V1 rather than building account linking just to support a more granular unsubscribe.
