using Microsoft.EntityFrameworkCore;
using NoonScraper.Data.Models;

namespace NoonScraper.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // Pending (0) or Running (3). Spelled out because a partial index's filter is
    // raw SQL; JobStatus documents why the numbers are what they are.
    private const string ActiveJobFilter = "\"Status\" IN (0, 3)";

    public DbSet<Product> Products => Set<Product>();

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    public DbSet<NotificationSubscription> NotificationSubscriptions => Set<NotificationSubscription>();

    public DbSet<DiscountFlag> DiscountFlags => Set<DiscountFlag>();

    public DbSet<RestockEvent> RestockEvents => Set<RestockEvent>();

    public DbSet<CheckNowRequest> CheckNowRequests => Set<CheckNowRequest>();

    public DbSet<ProductCrawlRequest> ProductCrawlRequests => Set<ProductCrawlRequest>();

    public DbSet<CrawlRun> CrawlRuns => Set<CrawlRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Url)
            .IsUnique();

        // Ends in Id so "the latest snapshot" has a total order even when two
        // snapshots share a timestamp - see PriceHistoryAnalyzer.
        modelBuilder.Entity<PriceSnapshot>()
            .HasIndex(s => new { s.ProductId, s.CrawledAt, s.Id });

        modelBuilder.Entity<PriceSnapshot>()
            .HasOne(s => s.Product)
            .WithMany(p => p.PriceSnapshots)
            .HasForeignKey(s => s.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NotificationSubscription>()
            .HasIndex(n => new { n.ProductId, n.TelegramChatId })
            .IsUnique();

        // The unique index above leads with ProductId, so it can't serve the
        // "everything this chat subscribed to" lookup that /stop does.
        modelBuilder.Entity<NotificationSubscription>()
            .HasIndex(n => n.TelegramChatId);

        modelBuilder.Entity<NotificationSubscription>()
            .HasOne(n => n.Product)
            .WithMany(p => p.NotificationSubscriptions)
            .HasForeignKey(n => n.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DiscountFlag>()
            .HasOne(d => d.Product)
            .WithMany(p => p.DiscountFlags)
            .HasForeignKey(d => d.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DiscountFlag>()
            .HasOne(d => d.TriggeringSnapshot)
            .WithMany()
            .HasForeignKey(d => d.TriggeringSnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        // A snapshot triggers at most one flag / one restock. Enforced by the
        // database, so a retried or overlapping crawl can't record it twice.
        modelBuilder.Entity<DiscountFlag>()
            .HasIndex(d => d.TriggeringSnapshotId)
            .IsUnique();

        modelBuilder.Entity<RestockEvent>()
            .HasOne(r => r.Product)
            .WithMany(p => p.RestockEvents)
            .HasForeignKey(r => r.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RestockEvent>()
            .HasOne(r => r.TriggeringSnapshot)
            .WithMany()
            .HasForeignKey(r => r.TriggeringSnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RestockEvent>()
            .HasIndex(r => r.TriggeringSnapshotId)
            .IsUnique();

        ConfigureJobRequest<CheckNowRequest>(modelBuilder, "UX_CheckNowRequests_ActivePerProduct");
        ConfigureJobRequest<ProductCrawlRequest>(modelBuilder, "UX_ProductCrawlRequests_ActivePerProduct");

        // At most one Running scheduled crawl at any time.
        modelBuilder.Entity<CrawlRun>()
            .HasIndex(r => r.Status)
            .IsUnique()
            .HasFilter("\"Status\" = 3")
            .HasDatabaseName("UX_CrawlRuns_OneRunning");
    }

    // Both request tables share their shape and their invariants: a request
    // belongs to a product, and a product has at most one active (Pending or
    // Running) request at a time. That partial unique index is what makes
    // dispatching idempotent - a double-click, a retry, or two API instances
    // racing can't produce two workflow runs for the same product.
    private static void ConfigureJobRequest<T>(ModelBuilder modelBuilder, string activeIndexName)
        where T : JobRequestBase
    {
        modelBuilder.Entity<T>()
            .HasIndex(r => r.ProductId)
            .IsUnique()
            .HasFilter(ActiveJobFilter)
            .HasDatabaseName(activeIndexName);

        // Serves the stale-request sweep and the "recent dispatches" budget.
        modelBuilder.Entity<T>()
            .HasIndex(r => new { r.Status, r.RequestedAt });

        modelBuilder.Entity<T>()
            .Property(r => r.ErrorMessage)
            .HasMaxLength(1000);

        modelBuilder.Entity<T>()
            .Property(r => r.FailureStage)
            .HasMaxLength(64);

        modelBuilder.Entity<T>()
            .HasOne(r => r.Product)
            .WithMany()
            .HasForeignKey(r => r.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
