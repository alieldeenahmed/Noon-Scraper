# Failure model

What can go wrong, what the system does about it, what the user is told, and which test proves it. The principle throughout: **every failure ends up somewhere a person can read it, and one bad item never takes the rest of the work down with it.**

## Job states

`Pending → Running → Completed | Failed` on `ProductCrawlRequest` and `CheckNowRequest` (shared columns in `JobRequestBase`). `Failed` carries `FailureStage`, a public-safe `ErrorMessage`, the GitHub run id (once a worker claims it), and a correlation id that is also in the dispatch payload, the run's name in the Actions list, and every log line the worker writes.

Every transition is one conditional `UPDATE ... WHERE Status = <expected>` (`JobLifecycle`). Nobody reads a status and then writes one, so two actors can't both win, and the affected-row count says who did.

| Transition | Who | Guard | If the guard fails |
|---|---|---|---|
| → `Pending` | API, on submit / check | partial unique index: one active request per product | the request that's already there is returned (no second dispatch) |
| `Pending → Running` | worker | `Status = Pending` | worker exits *successfully* without scraping (duplicate delivery, or the request was already expired) |
| `Running → Completed` | worker | `Status = Running` | result discarded and logged — the request was expired while the worker was busy, and everyone has already been told it failed |
| `Pending/Running → Failed` | worker, API, the workflow's cleanup step | `Status IN (Pending, Running)` | no-op: failing something already finished changes nothing |
| stale `→ Failed` | API (on every new request) and daily crawl (every run) | older than `StaleAfter` (15 min) | — |

A `GET` on a request also *reads* a past-deadline request as `Failed` (`JobLifecycle.View`), so the API never reports "still running" for something that isn't, even before anything has written it down.

## Failure stages

| Stage | Meaning | Written by |
|---|---|---|
| `dispatch` | GitHub rejected or couldn't be reached (after retries) | API |
| `budget` | over the hourly dispatch budget; not dispatched, daily crawl will cover it | API |
| `start` / `launch-browser` | failed loading the request / starting Chrome | worker |
| `scrape` | the page didn't yield a product (blocked, empty, markup changed, HTTP error) | worker |
| `persist` | scraped fine, couldn't be saved | worker |
| `cancelled` | the workflow was cancelled or hit its timeout and got a signal | worker |
| `timeout` | nothing picked it up, or the worker never reported back, within `StaleAfter` | API / daily crawl / read-time view |
| `workflow` | the workflow died before the crawler ran (Chrome install, build) | workflow's `fail-request` step |
| `result` | the stored result couldn't be read back | API |

The message on the row is deliberately generic for anything that isn't one of our own scrape exceptions (`ScrapeErrors.Describe`): exception text from libraries can contain connection details or file paths, so it goes to the log and the row says "Unexpected TimeoutException; see the workflow run logs." The public API never returns exception text.

## Retries

Retrying is for problems that can clear up, and it is bounded.

- **Scrapes:** `CrawlOptions.MaxAttempts` (2), delay `RetryDelay × attempt`, and *the page is reset between attempts* — a failed navigation can leave the tab in a state that fails the retry too. Only transient failures retry: timeouts, Playwright errors, an empty page (often an anti-bot interstitial), and HTTP 403/408/429/5xx. A parse error or a 404 does not — the page is what it is.
- **GitHub dispatch:** 3 attempts with 250 ms / 1 s backoff on network errors, 5xx, 429/408, and a 403 that carries `Retry-After` (GitHub's secondary rate limit). A 403 without it is a real permission problem and isn't retried. Retrying is safe because the request row already exists, the active-request index prevents a duplicate, and the worker claim makes a doubly-delivered dispatch run once.
- **Database:** the crawler's `DbContext` uses Npgsql's `EnableRetryOnFailure`. `ProductRecorder` additionally retries once when the API inserted the same URL between its check and its insert.

There is no retry of a whole *job* after the worker dies. That would need a real queue's redelivery; here, a dead request is expired and reported, and the daily crawl (for anything that's a tracked product) covers it.

## The scheduled crawl (`DailyCrawl`)

Isolation is per item. A failure inside one category or one product is caught, logged with its URL, counted, and the run moves on.

- Each product's reading is committed on its own transaction (`ProductRecorder`), so a crash or cancellation loses at most the product in flight, never a batch.
- A **time budget** (15 min) stops the run *starting* new work when spent; user-added products are ordered never-crawled first, then oldest reading first, so a backlog drains across runs instead of hitting the workflow's 20-minute hard timeout mid-product. The count of deferred products is recorded.
- Overlap is refused by the database: a `CrawlRun` row with `Status = Running` is unique, so a manual run started during the scheduled one exits cleanly. A run whose process vanished would leave that row forever, so the next run marks any `Running` row older than budget + 10 min as failed ("Presumed dead") before starting.
- **Exit code policy** is explicit, because "every failure was caught" must not mean "always green":

| Outcome | Exit code | Workflow |
|---|---|---|
| finished; failure rate below 20 %, no failed category | 0 | green (a `::warning` annotation if any product failed) |
| a category failed, or ≥ 20 % of attempted products failed | 1 | red, with a `::error` annotation |
| cancelled (SIGTERM/SIGINT — the workflow was cancelled or timed out) | 130 | red |
| bad command line | 64 | red |
| skipped because another run is active | 0 | green |

The 20 % threshold is configurable (`Crawl:MaxFailureRate`). A stray delisted product shouldn't turn every run red; a broken selector should.

## Cancellation and timeouts

SIGTERM and Ctrl-C cancel a token passed to every scrape and database call. Outcome writes deliberately use a *fresh* token, so a cancelled job still records that it was cancelled. Browser sessions are `await using`, so Chrome is closed on every exit path including cancellation and exceptions.

Timeouts are layered: per-page wait timeouts in the scrapers (15 s for a listing, 20 s for a product page to hydrate), the crawl's time budget, the workflow's `timeout-minutes` (10 for on-demand jobs, 20 for the daily crawl), and the API's stale-request expiry (15 min) as the backstop for anything that dies without a word — a runner that's killed outright can't run any cleanup step.

## The workflow's own failures

`check-now.yml` and `crawl-product.yml` end with a step that runs on `failure() || cancelled()` and calls `fail-request`, which closes the request if it's still open (stage `workflow`). That covers the cases the crawler can't: `dotnet build` failed, Chrome wouldn't install, the run was cancelled from the Actions UI. It is idempotent, so a request the crawler already closed is left alone. What it can't cover is a runner that's terminated without running any steps — that case falls through to the stale-request expiry, which is why the expiry exists.

The dispatch payload's `requestId` is passed to the shell through an environment variable and validated as an integer first, rather than interpolated into the command.

## Duplicates and races, and what stops them

| Scenario | What happens | Test |
|---|---|---|
| Double-click / client retry on submit or check | second call returns the first's request; one dispatch | `CheckNowTests`, `CreateProductTests` |
| Two simultaneous submissions of one URL | one row, one crawl request, one dispatch, 11 × `409` | `Concurrent_submissions_of_one_product_create_it_once` |
| GitHub delivers a dispatch twice / a run is re-run | second worker claims nothing and exits 0 | `DispatchedJobTests` |
| Scheduled crawl overlaps an on-demand crawl of the same product | serialized by the advisory lock; second sees the first's snapshot; one restock event at most | `ProductRecorderTests` |
| Two scheduled crawls at once | one runs, one exits cleanly | `DailyCrawlTests.Overlap` |
| Worker finishes after the API expired its request | late result discarded | `DispatchedJobTests` |
| Timed-out request blocks the product | expired on the next request; a new one is accepted | `JobLifecycleTests` |
| Dispatch fails | request closed `Failed` at stage `dispatch`, response says so, product row kept | `ProductsApiTests`, `GitHubDispatchServiceTests` |

## What isn't covered

See [known-limitations.md](known-limitations.md). The short version: a request whose workflow never starts is reported as failed only after `StaleAfter`, not immediately; a commit acknowledgement lost during a database retry could in theory record one reading twice; and a delisted product that already has history keeps failing (and being counted) on every daily run.
