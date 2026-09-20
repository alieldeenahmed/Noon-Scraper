using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoonScraper.Data;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Crawler;

public sealed record RecordResult(
    int ProductId, int SnapshotId, bool Created, bool Restocked, bool DiscountFlagged, NotifyResult Notifications);

// Writes one scraped reading of a product: find-or-create the product, append a
// PriceSnapshot, run the restock / fake-discount rules against the history, and
// then notify subscribers. It replaces the old static ProductUpserter, which did
// all of that without any protection against two crawls touching the same product.
//
// Concurrency model - the scheduled crawl can overlap an on-demand one, and a
// timed-out job can be retried while its predecessor is still running:
//   1. Everything that decides and writes (product row, snapshot, restock event,
//      discount flag) runs in ONE transaction that first takes a Postgres advisory
//      lock keyed on the product's URL. Two crawls of the same product therefore
//      run one after the other: the second sees the first's snapshot, so it can't
//      record a duplicate restock or judge a discount against stale history.
//   2. The advisory lock also covers a product that doesn't exist yet (there's no
//      row to lock). The only writer that doesn't take it is the API inserting a
//      submitted URL, and the unique index on Url catches that: the loser retries
//      once, as an update.
//   3. Notifications are sent AFTER the commit, and each is claimed with a
//      conditional update (see SubscriptionNotifier), so the network call never
//      holds a database lock and two crawls can't both send the same alert.
public sealed class ProductRecorder(
    AppDbContext db,
    SubscriptionNotifier notifier,
    TimeProvider clock,
    ILogger<ProductRecorder> logger)
{
    private sealed record Written(Product Product, int SnapshotId, bool Created, bool Restocked, bool Flagged);

    public async Task<RecordResult> RecordAsync(
        ScrapedProduct item, ProductSource source, Category? category, CancellationToken ct = default)
    {
        Validate(item);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var written = await WriteAsync(item, source, category, ct);
                var notifications = await NotifySafelyAsync(written.Product, item.Price, written.Restocked, ct);

                return new RecordResult(
                    written.Product.Id, written.SnapshotId, written.Created, written.Restocked, written.Flagged, notifications);
            }
            catch (DbUpdateException ex) when (attempt == 1 && IsDuplicateProductUrl(ex))
            {
                logger.LogInformation("Another writer created {Url} first; retrying as an update", item.Url);
            }
        }
    }

    // A reading that would corrupt history if stored: a zero or negative price
    // would be recorded as a crash to nothing and alert every subscriber.
    private static void Validate(ScrapedProduct item)
    {
        if (item.Price <= 0)
        {
            throw new ScrapeParseException($"Refusing to record a non-positive price ({item.Price}).");
        }

        if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Url))
        {
            throw new ScrapeParseException("Refusing to record a product with no name or URL.");
        }
    }

    private async Task<Written> WriteAsync(
        ScrapedProduct item, ProductSource source, Category? category, CancellationToken ct)
    {
        // Wrapped in the context's execution strategy so that, when the connection
        // is configured to retry transient failures (Neon waking from suspend), the
        // whole transaction is retried as a unit rather than half of it.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // Nothing tracked from a previous product - or from a failed attempt at
            // this one - may leak into this unit of work.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({item.Url}, 0))", ct);

            var now = clock.GetUtcNow();

            var product = await db.Products.FirstOrDefaultAsync(p => p.Url == item.Url, ct);
            var created = product is null;
            if (product is null)
            {
                product = new Product { Url = item.Url, Source = source, IsActive = true };
                db.Products.Add(product);
            }

            product.NoonProductId = item.NoonProductId;
            product.Name = item.Name;

            // A reading with no rating or seller (a listing tile, say) doesn't erase
            // what an earlier, fuller reading found.
            if (item.Rating is not null)
            {
                product.Rating = item.Rating;
            }

            if (category is not null)
            {
                product.Category = category;
            }

            if (item.MerchantName is not null)
            {
                product.MerchantName = item.MerchantName;
            }

            var restocked = await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, item.Stock, ct);

            var snapshot = new PriceSnapshot
            {
                ProductId = product.Id,
                Product = product,
                Price = item.Price,
                Stock = item.Stock,
                DiscountPercent = item.DiscountPercent,
                CrawledAt = now
            };
            db.PriceSnapshots.Add(snapshot);

            if (restocked)
            {
                db.RestockEvents.Add(new RestockEvent
                {
                    ProductId = product.Id,
                    Product = product,
                    TriggeringSnapshotId = snapshot.Id,
                    TriggeringSnapshot = snapshot,
                    DetectedAt = now
                });
            }

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(
                db, product, snapshot, item.Price, item.DiscountPercent, now, ct);
            if (flag is not null)
            {
                db.DiscountFlags.Add(flag);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new Written(product, snapshot.Id, created, restocked, flag is not null);
        });
    }

    // The reading is already committed by now; a failure to notify must not turn
    // it into a failed crawl (or, worse, a retried one that records it twice).
    private async Task<NotifyResult> NotifySafelyAsync(
        Product product, decimal price, bool restocked, CancellationToken ct)
    {
        try
        {
            return await notifier.NotifyAsync(product, price, restocked, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notifying subscribers of product {ProductId} failed after the reading was saved", product.Id);
            return new NotifyResult(0, 0, 0, 0);
        }
    }

    private static bool IsDuplicateProductUrl(DbUpdateException ex) =>
        DatabaseErrors.IsUniqueViolation(ex, "IX_Products_Url");
}
