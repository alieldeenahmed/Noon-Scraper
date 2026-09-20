using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Crawler.Jobs;

// Crawls one freshly-submitted product immediately, instead of leaving it as a
// bare row until the next scheduled crawl reaches it.
public sealed class CrawlProductJob(
    AppDbContext db,
    IScrapeSessionFactory sessions,
    ProductRecorder recorder,
    TimeProvider clock,
    IOptions<CrawlOptions> options,
    ILogger<CrawlProductJob> logger)
    : DispatchedJob<ProductCrawlRequest>(db, sessions, clock, options, logger)
{
    protected override string JobName => "crawl-product";

    protected override DbSet<ProductCrawlRequest> Requests => Db.ProductCrawlRequests;

    protected override Task<ProductCrawlRequest?> LoadAsync(int requestId, CancellationToken ct) =>
        Db.ProductCrawlRequests.AsNoTracking().Include(r => r.Product).FirstOrDefaultAsync(r => r.Id == requestId, ct);

    protected override async Task<bool> ExecuteAsync(
        ProductCrawlRequest request, IScrapeSession session, StageTracker stage, CancellationToken ct)
    {
        var product = request.Product!;

        stage.Enter(Stages.Scrape);
        var scraped = await ScrapeWithRetryAsync(
            async () => await session.ScrapeProductAsync(product.Url, ct)
                ?? throw new ScrapeNoDataException("The page had no product data (it may have been blocked)."),
            session, ct);

        stage.Enter(Stages.Persist);

        // Record against the row the user submitted, whatever spelling of the URL
        // the page reported back.
        scraped.Url = product.Url;
        var result = await recorder.RecordAsync(scraped, product.Source, category: null, ct);

        Logger.LogInformation(
            "Recorded snapshot {SnapshotId} (restocked: {Restocked}, discount flagged: {Flagged})",
            result.SnapshotId, result.Restocked, result.DiscountFlagged);

        return await CompleteAsync(request.Id);
    }

    // A submission whose page doesn't exist (404/410) and that has never been
    // crawled successfully is junk - a typo, a delisted item, a made-up code. It
    // stops being tracked, so the scheduled crawl doesn't visit it every day
    // forever. A product that already has history is left alone: one 404 must not
    // silently end tracking of something we have months of data on.
    protected override async Task OnFailedAsync(ProductCrawlRequest request, Exception failure)
    {
        if (failure is not ScrapeNavigationException { Status: 404 or 410 })
        {
            return;
        }

        var deactivated = await Db.Products
            .Where(p => p.Id == request.ProductId && p.IsActive && !p.PriceSnapshots.Any())
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), CancellationToken.None);

        if (deactivated > 0)
        {
            Logger.LogWarning("Product page does not exist; stopped tracking product {ProductId}", request.ProductId);
        }
    }
}
