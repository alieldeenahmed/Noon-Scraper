using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Crawler.Jobs;

public sealed record CrawlSummary(
    bool SkippedBecauseAnotherRunIsActive,
    bool Cancelled,
    int CategoriesAttempted,
    int CategoriesFailed,
    int ProductsAttempted,
    int ProductsSucceeded,
    int ProductsFailed,
    int ProductsDeferred)
{
    public static CrawlSummary Skipped { get; } = new(true, false, 0, 0, 0, 0, 0, 0);

    public double FailureRate => ProductsAttempted == 0 ? 0 : (double)ProductsFailed / ProductsAttempted;

    // Explicit rather than "exit 0 unless we crashed": a run in which scraping
    // broke has to be red in Actions, even though every failure was caught and
    // recorded. A single failed product among hundreds is not that; a failed
    // category or a high failure rate is.
    public int ExitCode(CrawlOptions options)
    {
        if (Cancelled)
        {
            return ExitCodes.Cancelled;
        }

        if (CategoriesFailed > 0 || FailureRate >= options.MaxFailureRate)
        {
            return ExitCodes.Failure;
        }

        return ExitCodes.Success;
    }

    public override string ToString() =>
        $"categories {CategoriesAttempted - CategoriesFailed}/{CategoriesAttempted} ok; " +
        $"products {ProductsAttempted} attempted, {ProductsSucceeded} ok, {ProductsFailed} failed, {ProductsDeferred} deferred";
}

// The scheduled crawl: the five tracked category pages, then every user-added
// product's own page.
//
// Failure isolation is the point of this class. Each category and each product is
// its own unit: a failure is caught, logged, counted, and the run moves on. Each
// product's reading is committed on its own (ProductRecorder), so a failure - or
// the run being killed - loses at most the product in flight, never a batch. The
// run has a time budget and stops starting new work when it's spent, oldest-crawled
// products first, so a backlog drains across runs instead of hitting the
// workflow's hard timeout mid-product.
public sealed class DailyCrawl(
    AppDbContext db,
    IScrapeSessionFactory sessions,
    ProductRecorder recorder,
    TimeProvider clock,
    IOptions<CrawlOptions> options,
    ILogger<DailyCrawl> logger)
{
    public static readonly IReadOnlyList<(Category Category, string Url)> Categories =
    [
        (Category.Mobiles, "https://www.noon.com/egypt-en/mobiles/"),
        (Category.Laptops, "https://www.noon.com/egypt-en/electronics-and-mobiles/computers-and-accessories/computers-new/laptops/"),
        (Category.SkinCare, "https://www.noon.com/egypt-en/eg-skin-care/"),
        (Category.HairCare, "https://www.noon.com/egypt-en/eg-hair-care/"),
        (Category.PersonalCare, "https://www.noon.com/egypt-en/eg-personal-care/")
    ];

    public async Task<CrawlSummary> RunAsync(long? gitHubRunId, CancellationToken ct)
    {
        var o = options.Value;
        var started = clock.GetUtcNow();

        await SweepStaleWorkAsync(started, o, ct);

        var run = new CrawlRun { StartedAt = started, GitHubRunId = gitHubRunId };
        db.CrawlRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { ConstraintName: "UX_CrawlRuns_OneRunning" })
        {
            // The unique index allows one Running crawl at a time, so this is a
            // scheduled run overlapping a manual one (or a re-run).
            logger.LogWarning("Another scheduled crawl is already running; exiting without crawling");
            db.ChangeTracker.Clear();
            return CrawlSummary.Skipped;
        }

        var runId = run.Id;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Job"] = "daily-crawl",
            ["CrawlRunId"] = runId,
            ["CorrelationId"] = run.CorrelationId,
            ["GitHubRunId"] = gitHubRunId
        });

        logger.LogInformation("Crawl run started (budget {Budget})", o.Budget);

        // Counts survive an exception, so a cancelled or crashed run still reports
        // how far it got.
        var tally = new Tally();
        CrawlSummary summary;
        try
        {
            await CrawlAsync(tally, started + o.Budget, o, ct);
            summary = tally.ToSummary(cancelled: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            summary = tally.ToSummary(cancelled: true);
        }
        catch (Exception ex)
        {
            // Not a per-item failure (those are handled inside): the run itself
            // broke - the browser wouldn't launch, the database is unreachable.
            logger.LogError(ex, "Crawl run failed");
            await FinishRunAsync(runId, JobStatus.Failed, tally.ToSummary(cancelled: false), $"Run failed: {ScrapeErrors.Describe(ex)}");
            throw;
        }

        var exitCode = summary.ExitCode(o);
        await FinishRunAsync(
            runId,
            exitCode == ExitCodes.Success ? JobStatus.Completed : JobStatus.Failed,
            summary,
            summary.Cancelled ? "Cancelled." : summary.ToString());

        logger.LogInformation("Crawl run finished: {Summary}", summary);
        return summary;
    }

    private sealed class Tally
    {
        public int CategoriesAttempted, CategoriesFailed, Attempted, Succeeded, Failed, Deferred;

        public CrawlSummary ToSummary(bool cancelled) =>
            new(false, cancelled, CategoriesAttempted, CategoriesFailed, Attempted, Succeeded, Failed, Deferred);
    }

    private async Task CrawlAsync(Tally tally, DateTimeOffset deadline, CrawlOptions o, CancellationToken ct)
    {
        await using var session = await sessions.OpenAsync(ct);

        // A product can appear in several widgets on one page, and in a category
        // and the user-added list; it's read once per run.
        var seen = new HashSet<string>();

        async Task<bool> TryRecordAsync(Func<Task> record, string url)
        {
            tally.Attempted++;
            try
            {
                await record();
                tally.Succeeded++;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                tally.Failed++;
                logger.LogError(ex, "Failed to record {Url}", url);
                return false;
            }
        }

        foreach (var (category, url) in Categories)
        {
            ct.ThrowIfCancellationRequested();

            if (clock.GetUtcNow() >= deadline)
            {
                logger.LogWarning("Time budget spent; not starting category {Category}", category);
                continue;
            }

            tally.CategoriesAttempted++;
            logger.LogInformation("Scraping category {Category}", category);

            CategoryScrapeResult result;
            try
            {
                result = await ScrapeWithRetryAsync(() => session.ScrapeCategoryAsync(url, ct), session, o, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                tally.CategoriesFailed++;
                logger.LogError(ex, "Failed to scrape category {Category}", category);
                await ResetQuietlyAsync(session, ct);
                continue;
            }

            logger.LogInformation(
                "Category {Category}: {Found} products, {Skipped} tiles skipped", category, result.Products.Count, result.Skipped.Count);
            foreach (var tile in result.Skipped)
            {
                logger.LogWarning("Skipped a {Category} tile ({Href}): {Reason}", category, tile.Href, tile.Reason);
            }

            foreach (var item in result.Products)
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Add(item.Url))
                {
                    await TryRecordAsync(() => recorder.RecordAsync(item, ProductSource.Seed, category, ct), item.Url);
                }
            }
        }

        // User-submitted products aren't on any category page, so each is read from
        // its own. Never-crawled first, then oldest reading first.
        var userAdded = await db.Products
            .AsNoTracking()
            .Where(p => p.Source == ProductSource.UserAdded && p.IsActive)
            .Select(p => new { p.Id, p.Url, LastCrawled = p.PriceSnapshots.Max(s => (DateTimeOffset?)s.CrawledAt) })
            .OrderBy(p => p.LastCrawled != null)
            .ThenBy(p => p.LastCrawled)
            .ToListAsync(ct);

        logger.LogInformation("Scraping {Count} user-added product(s)", userAdded.Count);

        for (var i = 0; i < userAdded.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (clock.GetUtcNow() >= deadline)
            {
                tally.Deferred = userAdded.Count - i;
                logger.LogWarning("Time budget spent; deferring {Count} product(s) to the next run", tally.Deferred);
                break;
            }

            var product = userAdded[i];
            if (!seen.Add(product.Url))
            {
                continue;
            }

            var recorded = await TryRecordAsync(async () =>
            {
                var scraped = await ScrapeWithRetryAsync(
                    async () => await session.ScrapeProductAsync(product.Url, ct)
                        ?? throw new ScrapeNoDataException("The page had no product data (it may have been blocked)."),
                    session, o, ct);

                // Update the row this product already has, whatever spelling of the
                // URL the page reported back.
                scraped.Url = product.Url;
                await recorder.RecordAsync(scraped, ProductSource.UserAdded, category: null, ct);
            }, product.Url);

            if (!recorded)
            {
                await ResetQuietlyAsync(session, ct);
            }
        }
    }

    private Task<T> ScrapeWithRetryAsync<T>(Func<Task<T>> scrape, IScrapeSession session, CrawlOptions o, CancellationToken ct) =>
        Retry.RunAsync(
            scrape,
            o.MaxAttempts,
            o.RetryDelay,
            clock,
            (ex, attempt) =>
            {
                logger.LogWarning("Scrape attempt {Attempt} failed ({Type}: {Message}); retrying with a fresh page",
                    attempt, ex.GetType().Name, ex.Message.Split('\n')[0]);
                return session.ResetAsync(ct);
            },
            ct);

    // A failed scrape can leave the page broken; reset it so the next product
    // starts clean. If even that fails the next scrape will fail and be counted.
    private async Task ResetQuietlyAsync(IScrapeSession session, CancellationToken ct)
    {
        try
        {
            await session.ResetAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not reset the page after a failure");
        }
    }

    // Anything left Pending or Running past its deadline is dead: close it out so
    // it stops blocking new requests for that product. Also a crawl run whose
    // process vanished without finishing (its Running row would otherwise make
    // every later run think it's still going).
    private async Task SweepStaleWorkAsync(DateTimeOffset now, CrawlOptions o, CancellationToken ct)
    {
        var checks = await JobLifecycle.ExpireStaleAsync(db.CheckNowRequests, now, o.StaleAfter, ct);
        var crawls = await JobLifecycle.ExpireStaleAsync(db.ProductCrawlRequests, now, o.StaleAfter, ct);

        var deadRunCutoff = now - (o.Budget + TimeSpan.FromMinutes(10));
        var runs = await db.CrawlRuns
            .Where(r => r.Status == JobStatus.Running && r.StartedAt < deadRunCutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, JobStatus.Failed)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now)
                .SetProperty(r => r.Summary, "Presumed dead: still marked Running long after its time budget."), ct);

        if (checks + crawls + runs > 0)
        {
            logger.LogWarning(
                "Expired stale work: {Checks} check-now request(s), {Crawls} product crawl request(s), {Runs} crawl run(s)",
                checks, crawls, runs);
        }
    }

    private async Task FinishRunAsync(int runId, JobStatus status, CrawlSummary summary, string text)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await db.CrawlRuns
                .Where(r => r.Id == runId && r.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)clock.GetUtcNow())
                    .SetProperty(r => r.CategoriesAttempted, summary.CategoriesAttempted)
                    .SetProperty(r => r.CategoriesFailed, summary.CategoriesFailed)
                    .SetProperty(r => r.ProductsAttempted, summary.ProductsAttempted)
                    .SetProperty(r => r.ProductsSucceeded, summary.ProductsSucceeded)
                    .SetProperty(r => r.ProductsFailed, summary.ProductsFailed)
                    .SetProperty(r => r.ProductsDeferred, summary.ProductsDeferred)
                    .SetProperty(r => r.Summary, JobLifecycle.Truncate(text)), cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the crawl run's outcome");
        }
    }
}
