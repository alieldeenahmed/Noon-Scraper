# Engineering log: obstacles and how I solved them

This is a record of the real problems I ran into building this, in the order I hit them, and what actually fixed each one. I'm keeping it because most of these took real investigation to get to the bottom of, and the "what I tried that didn't work" part is as informative as the fix.

## 1. The site turned out to be protected by Akamai, not Cloudflare

I scoped this project assuming Noon was behind Cloudflare with moderate rate limiting — a realistic-but-manageable target, harder than a site with an open API, easier than TikTok-tier fingerprinting. The first real crawl attempt (a plain Playwright + Chromium request, no mitigations) came back with a 2.6KB page instead of the actual listing. The response body was a `sec-if-cpt-container` div and the text "Powered and protected by Akamai" — Akamai Bot Manager, not Cloudflare. That's a meaningfully different (and generally tougher) target than what I'd planned around.

**Fix:** rather than treat this as a blocker, I added stealth mitigations to mask the cheap, static automation signals bot-detection scripts check for before any real interaction happens:

- `navigator.webdriver` overridden to `undefined`
- a fake `window.chrome.runtime` object (headless/automated Chrome doesn't have one by default)
- `navigator.plugins` and `navigator.languages` populated to look like a normal browser profile
- launch flags disabling the `--enable-automation` flag and blink's automation-controlled feature

With that in place, the exact same request that got blocked returned the real page — full category listing, real products. That confirmed the block was largely the static fingerprint, not deep behavioral analysis, which made the whole project feasible again.

## 2. Headless Chrome is hard-blocked; headful works

Once the stealth script was proven working, I tested `Headless = true` (needed eventually, since GitHub Actions runners have no display). It failed instantly and deterministically with `net::ERR_HTTP2_PROTOCOL_ERROR` — a connection-level failure before any page content loads at all, not a JS challenge. I reran it twice to rule out a network fluke; both times, identical failure. Switching back to `Headless = false` with the same stealth script worked immediately.

This means Akamai is fingerprinting something at the TLS/HTTP2 connection layer that differs between headless and headful Chrome — not something a JS-level fix can patch. The practical implication: every environment that runs this scraper needs a real, visible Chrome instance, which for a display-less environment (CI, WSL, a server) means running under **Xvfb** (a virtual framebuffer) rather than true headless mode. Xvfb-wrapped headful Chrome has the same network fingerprint as a real headful browser — it's just rendering to a fake display instead of a real one.

## 3. Windows Smart App Control blocked the compiled crawler outright

Locally on Windows, running the Crawler for the first time failed with:

```
System.IO.FileLoadException: Could not load file or assembly '...NoonScraper.Crawler.dll'.
An Application Control policy has blocked this file. (0x800711C7)
```

This is Windows 11's Smart App Control — a reputation-based system that blocks any binary it doesn't recognize. The problem isn't specific to this project: **any freshly-compiled .NET binary has zero reputation by definition**, since the compiler embeds a unique build ID into the assembly on every build, so even re-running the exact same source produces a "new," unrecognized file each time. Unlike a normal Defender detection, Smart App Control in its "On" state doesn't offer a per-app "allow anyway" override through the UI at all — I confirmed this by checking Protection History, which showed the block but had no working allow action.

I considered turning Smart App Control off entirely, but it's a **one-way decision** — Microsoft doesn't provide a way to turn it back on without a clean Windows reinstall. That's a permanent security tradeoff for what amounts to local build friction, not an actual security event, so I ruled it out.

**Fix:** run the Crawler (and anything else that launches a browser) from **WSL2** instead of native Windows. Smart App Control only polices Windows executables — a Linux binary running under WSL is completely outside its scope. This also turned out to be useful beyond just working around the block, since it's a closer match to the Ubuntu environment GitHub Actions will actually run the cron job on.

## 4. The default WSL "Ubuntu" install was too new for Playwright's tooling

Running `wsl --install -d Ubuntu` (the generic alias, no version pinned) installed **Ubuntu 26.04** — a release new enough that Playwright's own dependency installer didn't recognize it yet:

```
ERROR: Playwright does not support ffmpeg on ubuntu26.04-x64
Cannot install dependencies for ubuntu26.04-x64 with Playwright 1.55.0-beta-1756314050000!
```

**Fix:** install `Ubuntu-24.04` specifically (`wsl --install -d Ubuntu-24.04`) rather than the generic alias. This also happens to match the Ubuntu version GitHub Actions runners commonly use, so it's the more correct choice for this project regardless of the Playwright compatibility issue.

## 5. Building from a Windows-mounted path inside WSL fails silently-ish

With WSL and .NET both working, `dotnet run` from the project's normal location (`/mnt/g/Code projects/Noon Scraper/Backend/...`) failed with:

```
error MSB4018: The "CreateAppHost" task failed unexpectedly.
System.ComponentModel.Win32Exception (1): Could not set file permission 755 for .../obj/Debug/net9.0/apphost.
```

This is a known limitation of WSL's DrvFs filesystem driver — Unix file permissions (`chmod +x`) can't always be reliably set on files that physically live on a Windows NTFS drive mounted into WSL, even though most file operations work fine there.

**Fix:** `rsync` the project into WSL's own native filesystem (`~/noon-scraper/Backend`) and build/run from there instead of the `/mnt/g/...` path. This is also meaningfully faster — cross-filesystem I/O between WSL and the Windows host has real overhead, so builds and restores are noticeably quicker from the native path too. The tradeoff is a manual sync step (`rsync --exclude bin/ --exclude obj/`) any time source files change on the Windows side before running something in WSL.

## 6. No PowerShell on Linux to run Playwright's install script

Playwright's .NET package generates a `playwright.ps1` script for installing browsers, meant to be run via `pwsh playwright.ps1 install chrome`. A fresh Ubuntu install has no PowerShell at all, and installing one just to run a two-line script felt like unnecessary overhead.

Reading `playwright.ps1` showed it does exactly one thing: load `Microsoft.Playwright.dll` and call `Microsoft.Playwright.Program.Main(args)` — a public static method. Since the Crawler project already references that same package directly, I added a tiny temporary passthrough at the top of `Program.cs`:

```csharp
if (args.Length > 0 && (args[0] == "install" || args[0] == "install-deps"))
{
    Environment.Exit(Microsoft.Playwright.Program.Main(args));
}
```

That let me run `dotnet run -- install chrome` and `dotnet run -- install-deps` directly — no PowerShell needed. I removed this passthrough once the browser and system dependencies were actually installed; it was scaffolding, not a permanent feature.

Both `install` and `install-deps` need `sudo` internally (they shell out to `apt`/`dpkg`), which can't be supplied non-interactively — I ran those two commands myself, once, in an interactive terminal where I could type my password when prompted.

## 7. A real data-integrity bug: duplicate products from tracking query strings

While testing the user-submitted-URL flow, I found **10 duplicate `Product` rows for the exact same real-world item** (same `NoonProductId`), each with a slightly different `Url`. The cause: Noon appends a per-crawl-session tracking query string (`?o=...&pcl=...`) to every product link. Since the uniqueness check was on the raw URL *including* that query string, every crawl that revisited the same product saw what looked like a "new" URL and inserted a duplicate row instead of updating the existing one. This had been happening silently across every test run up to that point — it's also why price history for a product was getting split across multiple phantom rows instead of accumulating on one, and why the `POST /products` duplicate check could be fooled by a resubmission carrying a different tracking token.

**Fix:** a shared `UrlNormalizer.Normalize()` (in `NoonScraper.Data`, so both the Crawler and the API use identical logic) strips a URL down to scheme + host + path before it's ever written or compared — in `CategoryScraper`, `ProductPageScraper`, and the `POST /products` endpoint. Since all data up to that point was pre-launch test data anyway, I wiped `Products`/`PriceSnapshots` and re-crawled clean rather than writing migration logic to reconcile the duplicates. After the fix: 107 unique products, zero duplicate `NoonProductId` groups, confirmed by query.

One nuance that came up later (see #9): that "tracking" query parameter isn't pure noise — it can also function as an *offer selector* distinguishing which specific seller's listing a URL points to. Stripping it is still correct for identifying "the same product" at the `Product`-table level; it just means the parameter has a second, more meaningful job I didn't know about when I first normalized it away.

## 8. Finding a reliable data source on the product detail page

The category listing pages (used for the initial 5 tracked categories) expose product tiles with `data-qa` attributes that are stable enough to select on directly (`product-box-name`, `product-box-price`, etc.). The individual product **detail** page — needed for user-submitted URLs — turned out to be a different story:

- No `data-qa` attribute for the product name or rating at all.
- Rating is rendered as a CSS `--filled-width` percentage on a star icon, not a plain number — not something worth reverse-engineering into an exact decimal.
- No merchant/seller name in the initial page load — it only appears in the fully hydrated DOM using build-hashed CSS-module class names (`_soldBy_gxmen_66`, etc.) that could change on any Noon frontend redeploy.

Rather than build something fragile against hashed class names, I checked for `application/ld+json` script blocks and found Noon embeds a full **schema.org `Product`** structured-data object on every detail page — `name`, `sku`, `aggregateRating.ratingValue`, `offers.price`, `offers.priceSpecification.price` (the pre-discount price), `offers.availability` (`InStock`/`OutOfStock`), and `offers.seller.name`. That one JSON block gave clean, semantic answers to three problems I hadn't fully solved yet: the actual product name, a real numeric rating, and — genuinely useful — **actual stock availability**, which nothing on the category listing pages exposed at all.

`ProductPageScraper` parses this JSON-LD directly instead of touching the DOM for any of it. The only DOM interaction left is waiting for `[data-qa='div-price-now']` to confirm the page has hydrated before reading the script tag.

I verified this end-to-end by submitting a real product (an Anker USB-C charger) through `POST /products`, then running the crawler and confirming it correctly filled in the name, a real third-party merchant ("Dokkan Tech"), rating, price, live stock status, and discount percentage — all from that one JSON-LD block.

## 9. Restock and fake-discount detection needed synthetic data to verify

Both detection rules (`PriceHistoryAnalyzer`) depend on comparing a new snapshot against a product's prior history — and since real crawling had only been running for a few hours at that point, there wasn't enough natural history for either rule to fire. To confirm the logic actually worked rather than just compiled, I inserted synthetic historical `PriceSnapshot` rows directly via SQL: an artificially low price, a later "spike," and an out-of-stock snapshot, timed to sit just before a real crawl run.

The first two attempts at this test produced no detection at all — not because the logic was wrong, but because my synthetic timestamps (`NOW() - INTERVAL '1 hour'`, etc.) landed *before* real snapshots that already existed from earlier testing, so they weren't actually "the most recent prior snapshot" by the time the query ran. Once I inserted the synthetic out-of-stock row with a bare `NOW()` timestamp immediately before running the crawl, both rules fired correctly: a `RESTOCK` log line, and a `SUSPICIOUS DISCOUNT` flag with the correct triggering snapshot, prior high price, and discount percentage recorded in `DiscountFlags`. I deleted the synthetic rows afterward so they wouldn't pollute real data.

One limitation I'm keeping as-is rather than over-engineering: the fake-discount rule re-flags the same product on *every* crawl while the underlying pattern persists, rather than deduping to a single flag. Given this is explicitly a stretch feature, that's an acceptable simplification for now — worth revisiting only if it turns out to be noisy in practice.

## 10. Cross-merchant comparison: confirming it was even possible before building it

The original plan included an on-demand "check now" action that compares prices across multiple sellers of the same product. Before writing any code for it, I checked whether Noon actually exposes multiple sellers per product at all — the JSON-LD from #8 only ever shows a single `offers` object, which was a real open question, not an assumption I wanted to build on blind.

Searching the full rendered page for "other seller" turned up a `_otherOffersCard_` component with the text "More offers from other sellers" — a collapsed panel, not part of the initial page load. Clicking it (via Playwright) and capturing the resulting DOM confirmed the feature is real: for a commodity item like a USB-C charger, the expanded panel listed three genuinely different sellers — Dokkan Tech, Wi-Tech, and Digital Luxury For General Trading — each with its own price, seller rating, and review count in a consistent card structure. A single-seller item (like a phone sold directly by Noon itself) simply doesn't have this trigger, or shows only the one seller — a valid, expected outcome, not a failure case.

Two things worth noting from this investigation:
- The panel hydrates **after** the initial price data does — checking for the trigger immediately (the same mistake as earlier waits) silently produced only the single default offer instead of the full list. The fix was the same pattern as before: wait for network idle plus a fixed buffer before checking whether the trigger exists.
- The `?o=...` query parameter I'd stripped away in #7 turned out to double as an **offer selector** — each seller's card links to the same product URL with a different `o` value, which is how Noon's frontend picks which offer to display by default. That doesn't change the URL-normalization fix (a `Product` row still needs to key on scheme+host+path only), but it explained a data point that had briefly looked like unexplained noise.

**Result:** `OfferScraper` scrapes every seller card when the panel exists, and falls back to the JSON-LD default offer for genuinely single-seller products. This runs synchronously inside `POST /api/products/{id}/check-now` — meaning the API itself now launches its own headful Chrome session per request (via a `StealthBrowserSession` helper, the same stealth setup as the Crawler), not stored anywhere, since this is explicitly a one-off check rather than continuous background tracking. Verified against three real cases: a product with 3 competing sellers, a product that started single-seller in earlier testing but had gained a real second seller by the time I retested it, and a 404 for an untracked product ID.

This also means deploying the API to Render will need to account for the same headful-Chrome requirement as the Crawler (#2) — almost certainly a custom Docker image with Xvfb rather than Render's default .NET runtime. That's an open item for the deployment step, not solved yet.

## Summary of what's still open

- Telegram notifications (bot setup, chat-id capture, subscribe/unsubscribe, and the actual notify-on-event logic) — the data model exists, nothing sends a message yet.
- The GitHub Actions cron workflow itself — the crawler works, but nothing runs it on a schedule yet. It'll need Ubuntu 24.04 pinned explicitly (not `ubuntu-latest`, per #4), Xvfb (per #2), and Chrome + its system dependencies installed fresh each run.
- Render deployment for the API, complicated by the headful-Chrome requirement from #10.
