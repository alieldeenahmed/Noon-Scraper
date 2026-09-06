# Scheduled crawl (GitHub Actions)

The daily crawl runs via [`.github/workflows/daily-crawl.yml`](../../.github/workflows/daily-crawl.yml) at the repo root (GitHub requires workflow files to live there, not next to the code they run).

## What it does

1. Checks out the repo on an **`ubuntu-24.04`** runner — pinned explicitly rather than `ubuntu-latest`. I hit a real version mismatch during local setup where the newest Ubuntu release broke Playwright's dependency installer (see `engineering-log.md`, #4); pinning avoids that happening again the moment GitHub rolls `ubuntu-latest` forward.
2. Sets up .NET 9, builds the Crawler project.
3. Installs Chrome and Playwright's system dependencies using the same `dotnet run -- install chrome` / `install-deps` commands I use locally — not raw `apt` commands hand-written into the YAML, so there's exactly one source of truth for "what browser and dependencies does this need," shared between local dev and CI.
4. Runs the crawler wrapped in **`xvfb-run`**. The target site's anti-bot protection hard-blocks headless Chrome at the network layer (`engineering-log.md`, #2) — real headful Chrome is required, and `xvfb-run` gives it a virtual display on a runner that has no real one. This is not the same thing as headless mode: it's the actual headful browser binary, same network fingerprint, just rendering to a fake display.
5. Passes the database connection string in as `ConnectionStrings__DefaultConnection` (the double-underscore is .NET's convention for nested config keys via environment variables), sourced from a GitHub Actions secret rather than committed anywhere.

## The one manual step

GitHub Actions can't know the database connection string on its own — it has to be added as a repository secret once:

1. On GitHub: **Settings → Secrets and variables → Actions → New repository secret**.
2. Name: `DATABASE` (the name is arbitrary — it just has to match whatever the workflow file references in `secrets.<name>`).
3. Value: the same Npgsql-format connection string used locally (`Host=...;Database=...;Username=...;Password=...;SSL Mode=Require` — not the raw Neon `postgresql://` URI, see `database-setup.md` for why that distinction matters).

Without this secret set, the workflow will run and fail at the crawl step with a connection error — everything before that (build, Chrome install) will still succeed, which is a useful way to tell the two failure modes apart if something goes wrong.

## Schedule and manual runs

The cron trigger (`0 3 * * *`, 03:00 UTC daily) is intentionally outside typical peak traffic hours for the site being crawled. The workflow also accepts `workflow_dispatch`, so it can be triggered manually from the Actions tab for testing without waiting for the schedule.

## Cost

GitHub Actions' free tier includes 2000 minutes/month for private repos (unlimited for public ones). A single run — .NET setup, build, Chrome + dependency install, then the actual crawl — comfortably fits inside the 20-minute timeout I set on the job as a safety net against a hang burning through the monthly quota unnoticed.
