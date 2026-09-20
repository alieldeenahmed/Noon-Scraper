# Security model

The API is a public demo: anyone can browse, submit a product link, and start a comparison, with no account. That shapes the whole model. There is no login to protect and none to bypass — the things worth protecting are **(a) the metered GitHub Actions budget, (b) the crawl workload every submitted product adds forever, (c) the crawler's browser, which visits whatever URL it's given, and (d) the Telegram webhook.** So the controls are about bounding what an anonymous caller can cause, not about identity.

## Assets and what bounds them

| Asset | Threat | Control |
|---|---|---|
| GitHub Actions minutes | a caller (or a script) triggers thousands of workflow runs | DB-backed budgets: at most 30 dispatches/hour across all callers (`Jobs:MaxDispatchesPerHour`); one active request per product; in-process rate limits as a first line |
| Crawl workload | a caller adds thousands of products, all re-crawled daily | at most 20 new products/hour and 500 active user-added products (`Jobs:MaxNewProductsPerHour`, `Jobs:MaxUserProducts`); the daily crawl has a time budget and drains oldest-first |
| The crawler's browser (SSRF-style) | a submitted URL points the crawler at an internal address, another site, or a lookalike | strict allow-listed URL validation (below), and the crawler only ever visits the *canonical URL rebuilt from the parsed parts*, never the raw string |
| Telegram webhook | anyone POSTs fake updates to subscribe/unsubscribe arbitrary chats | shared-secret header, fail-closed, constant-time compare |
| Error detail | exceptions leak connection strings or paths | generic public messages; detail to logs only |

## URL validation (`NoonUrl`)

The single definition of "a product link", used by the API and by every place a URL is stored or compared. A link is accepted only if **all** of these hold:

- at most 2048 characters, with no whitespace, control or invisible (Unicode "format", e.g. zero-width) characters — a way to make two different strings look identical;
- `https`, no userinfo (`https://noon.com@evil.com/...`), no non-default port;
- host is exactly `noon.com` or `www.noon.com`, compared on the ASCII/punycode form so a lookalike in another script can't compare equal (an earlier version used `EndsWith("noon.com")`, which accepted `evilnoon.com` — engineering log #16);
- path shaped like a product page: 3–5 segments, ending `/p`, a product code that is 2–64 alphanumerics, a market segment like `egypt-en`, every other segment restricted to letters (any script — Arabic slugs are common), digits and a few punctuation marks. It's checked against the path *as the user wrote it*, because `Uri` silently percent-encodes characters such as `<`, `>`, `"` and `|`, which would otherwise pass a whitelist applied afterwards;
- the query string and fragment are dropped, and the host is forced to `www.noon.com`.

The same product in the same market is recognized under a different spelling (the Arabic page of an item already tracked in English) and returns `409` with the existing product's id, so it can't be added twice. Route ids are integers (`{id:int}`), and check-now results are looked up by *both* request id and product id, so one product's id can't be used to read another's request.

## Rate limiting and forwarded IPs

In-process, fixed one-minute windows, configurable under `RateLimiting:*`: 300 requests/IP, 3000 combined, and for the endpoints that dispatch GitHub runs 5/IP and 30 combined. **These are a first line of defence against a noisy client and nothing more.** They live in this process's memory, so they reset on restart and are per-instance; and the per-IP ones are only as trustworthy as the client IP, which behind Back4app's proxy chain of unknown depth comes from a client-forgeable `X-Forwarded-For`. The combined buckets don't depend on the IP, and they also cap how many distinct per-IP limiters a caller can make the process create. `Network:ForwardLimit` pins the number of trusted proxy hops once it's known.

The limits that actually protect money are the **database-backed** ones above (`JobRequestService`). They survive restarts, hold across instances, and don't read a header at all — so rotating `X-Forwarded-For` gets an attacker nothing against the Actions budget.

The limiter sits after CORS so browsers can read a `429`, and rejections carry `Retry-After`.

## Why there's no API key

I considered a lightweight key on the dispatch endpoints and decided against it. The whole point of the deployment is that a visitor can try it with no signup, so a key would have to be embedded in the frontend bundle — where it protects nothing, since anyone can read it. The dispatch endpoints are instead made safe to leave open: idempotent per product, budgeted globally in the database, and unable to be pointed at anything but a noon.com product page. If this ever had non-anonymous users, that's where authentication would go.

## Telegram webhook

- **Fail closed:** with no `Telegram:WebhookSecret` configured, the endpoint returns `503` outside `Development` instead of accepting everyone. (Locally it's open so it can be tried without setup.)
- The secret is compared with `CryptographicOperations.FixedTimeEquals` (constant time), so it can't be guessed byte by byte from response timing.
- Only text messages in **private** chats are acted on; groups, channels and everything else are acknowledged and ignored (a non-2xx would make Telegram redeliver).
- Subscribing needs an existing product, is idempotent (one subscription per product and chat), and a chat can hold at most 25, so a chat can't be used to fan out unbounded work.
- The bot token is never in a log line: it travels in Telegram's URL path, so the HTTP client's own request logging is filtered to warnings in both the API and the crawler.

## Everything else

- Request bodies are capped at 64 KB (Kestrel) — the API takes a URL, not uploads.
- CORS trusts only the deployed frontend origin outside `Development`.
- Unhandled exceptions return an RFC 7807 problem body with a trace id and no exception text; the trace id is what ties a user's report to a log line.
- Secrets (database URL, GitHub dispatch token, Telegram token and webhook secret) come from user-secrets locally and platform environment variables in production; none are in the repository, and the test suite blanks them so a test can never use one that happens to be on a developer's machine.
- The dispatch token is a fine-grained GitHub PAT limited to this one repository (see `Backend/README.md` for the setup); the GitHub API's error body is logged truncated and never returned to the caller.

## Not covered

Being explicit, because a security section that only lists strengths isn't one:

- **No authentication or authorization**, by design (see above). Anyone can add products up to the caps and can see every tracked product.
- **A determined caller can still use the budgets up**, denying the feature to others until the hour rolls over. The caps bound the *cost*; they don't stop the abuse.
- **In-memory rate limits are per instance and forgeable per IP** (above).
- **No CAPTCHA, no abuse reporting, no way to remove a product** a user shouldn't have added, short of editing the database.
- The crawler visits the canonical URL, but a noon.com page could itself redirect elsewhere; I rely on Chrome's normal behaviour and the fact that it only reads the resulting page.
- I have not had this reviewed by anyone else or penetration-tested. It's a careful self-audit plus tests, not an assurance.
