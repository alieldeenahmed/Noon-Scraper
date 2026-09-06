using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Models;

namespace NoonScraper.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

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
    }
}
