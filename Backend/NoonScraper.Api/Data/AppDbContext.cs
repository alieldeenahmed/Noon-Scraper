using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Models;

namespace NoonScraper.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Url)
            .IsUnique();
    }
}
