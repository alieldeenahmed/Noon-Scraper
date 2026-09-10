# Database setup

## Why Neon, not local Postgres

The daily crawl runs on GitHub Actions, which can't reach a database sitting on my own machine. That ruled out a local Postgres install for anything beyond quick experiments. Render's free Postgres tier was the other obvious option, but it expires after 90 days — not something I wanted to build a portfolio project's data around. Neon's free tier is always-on and reachable from anywhere, which is what the whole "scheduled cloud crawl" design depends on.

## One-time project setup

1. Create a Neon account and project at [neon.tech](https://neon.tech).
2. Install the Neon CLI and link this repo to the project:
   ```bash
   npm i -g neon@latest
   neon login
   neon link --project-id <project-id> --branch production
   ```
   `neon link` writes the real connection string into `.env.local` and a `.neon` config file — both are git-ignored, never committed.
3. `neon config init` scaffolds a `neon.ts` policy file (kept minimal — an empty `defineConfig({})`, no per-branch overrides needed for a single-developer project).

## Applying the schema

The five models — `Product`, `PriceSnapshot`, `NotificationSubscription`, `DiscountFlag`, `RestockEvent` — live in `NoonScraper.Data`, along with `AppDbContext`. Schema changes go through EF Core migrations rather than hand-written SQL:

```bash
cd NoonScraper.Api          # the project used as the migration "startup project"
dotnet ef migrations add <MigrationName> --project ../NoonScraper.Data --startup-project .
dotnet ef database update --project ../NoonScraper.Data --startup-project .
```

The startup project needs the `Microsoft.EntityFrameworkCore.Design` package (only there, not in `NoonScraper.Data` — keeping the Design tooling out of the class library that actually ships is the standard EF Core split). The connection string comes from whichever project's user secrets are active — see the root README's Configuration section.

## Connection string format gotcha

Neon's CLI and dashboard both give you a connection string in the Node/`psql` URI style:

```
postgresql://user:password@host/dbname?sslmode=require
```

Npgsql (the .NET Postgres driver) doesn't parse that format — it wants semicolon-delimited keywords instead:

```
Host=<host>;Database=<dbname>;Username=<user>;Password=<password>;SSL Mode=Require
```

Trying to use the raw Neon-style string directly throws inside `NpgsqlConnectionStringBuilder` with a fairly unhelpful `KeyNotFoundException`. Converting the format fixes it immediately.

Also worth knowing: Neon connection string passwords are case-sensitive and visually easy to mistype when reading them off a terminal (e.g. `tKcXN5ak0nHe`, not `tkcxn5ak0nhe`) — I hit exactly this once and got a `28P01: password authentication failed` error that had nothing to do with the format fix above. Copy the password directly out of `.env.local` rather than retyping it from what's printed on screen.

## Verifying the schema

Neon's CLI can run `psql` against the linked project directly, which is the fastest way to confirm a migration actually landed correctly:

```bash
neon psql production -- -c "\dt"
neon psql production -- -c "\d \"Products\""
```

(On a machine without a local `psql` binary, the CLI transparently falls back to an embedded implementation — no extra install needed.)
