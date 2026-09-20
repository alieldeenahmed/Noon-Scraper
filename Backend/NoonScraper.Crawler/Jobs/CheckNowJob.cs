using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Crawler.Jobs;

// The on-demand cross-merchant comparison: scrape every seller's offer for one
// product and store the result on the request for the API to serve.
public sealed class CheckNowJob(
    AppDbContext db,
    IScrapeSessionFactory sessions,
    TimeProvider clock,
    IOptions<CrawlOptions> options,
    ILogger<CheckNowJob> logger)
    : DispatchedJob<CheckNowRequest>(db, sessions, clock, options, logger)
{
    protected override string JobName => "check-now";

    protected override DbSet<CheckNowRequest> Requests => Db.CheckNowRequests;

    protected override Task<CheckNowRequest?> LoadAsync(int requestId, CancellationToken ct) =>
        Db.CheckNowRequests.AsNoTracking().Include(r => r.Product).FirstOrDefaultAsync(r => r.Id == requestId, ct);

    protected override async Task<bool> ExecuteAsync(
        CheckNowRequest request, IScrapeSession session, StageTracker stage, CancellationToken ct)
    {
        stage.Enter(Stages.Scrape);
        var offers = await ScrapeWithRetryAsync(
            () => session.ScrapeOffersAsync(request.Product!.Url, ct), session, ct);

        if (offers.Count == 0)
        {
            throw new ScrapeNoDataException("No offers were found on the product page.");
        }

        Logger.LogInformation("Found {Count} offer(s)", offers.Count);

        stage.Enter(Stages.Persist);
        var resultJson = JsonSerializer.Serialize(offers.OrderBy(o => o.Price));

        // Only if still Running: see DispatchedJob.
        var rows = await Db.CheckNowRequests
            .Where(r => r.Id == request.Id && r.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, JobStatus.Completed)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)Clock.GetUtcNow())
                .SetProperty(r => r.ResultJson, resultJson), CancellationToken.None);

        return rows == 1;
    }
}
