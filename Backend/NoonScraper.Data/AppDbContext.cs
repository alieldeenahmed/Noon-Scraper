using Microsoft.EntityFrameworkCore;
using NoonScraper.Data.Models;

namespace NoonScraper.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    public DbSet<NotificationSubscription> NotificationSubscriptions => Set<NotificationSubscription>();

    public DbSet<DiscountFlag> DiscountFlags => Set<DiscountFlag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Url)
            .IsUnique();

        modelBuilder.Entity<PriceSnapshot>()
            .HasIndex(s => new { s.ProductId, s.CrawledAt });

        modelBuilder.Entity<PriceSnapshot>()
            .HasOne(s => s.Product)
            .WithMany(p => p.PriceSnapshots)
            .HasForeignKey(s => s.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NotificationSubscription>()
            .HasIndex(n => new { n.ProductId, n.TelegramChatId })
            .IsUnique();

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
    }
}
