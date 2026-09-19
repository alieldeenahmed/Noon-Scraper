using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

public static class TestDb
{
    public static AppDbContext Create() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public static Product AddProduct(
        AppDbContext db,
        string url = "https://www.noon.com/egypt-en/some-product/N1/p/",
        string? name = "Some Product",
        Category? category = null,
        ProductSource source = ProductSource.Seed)
    {
        var product = new Product { Url = url, Name = name, Category = category, Source = source };
        db.Products.Add(product);
        db.SaveChanges();
        return product;
    }

    public static PriceSnapshot AddSnapshot(
        AppDbContext db,
        Product product,
        decimal price,
        bool stock = true,
        DateTimeOffset? crawledAt = null,
        decimal? discountPercent = null)
    {
        var snapshot = new PriceSnapshot
        {
            ProductId = product.Id,
            Price = price,
            Stock = stock,
            DiscountPercent = discountPercent,
            CrawledAt = crawledAt ?? DateTimeOffset.UtcNow
        };
        db.PriceSnapshots.Add(snapshot);
        db.SaveChanges();
        return snapshot;
    }
}

public sealed class FakeDispatchService() : GitHubDispatchService(new HttpClient(), new ConfigurationBuilder().Build())
{
    public List<int> CrawledProductIds { get; } = [];

    public List<int> CheckedRequestIds { get; } = [];

    public bool ShouldFail { get; set; }

    public override Task TriggerCrawlProductAsync(int productId)
    {
        if (ShouldFail)
        {
            throw new HttpRequestException("simulated GitHub failure");
        }

        CrawledProductIds.Add(productId);
        return Task.CompletedTask;
    }

    public override Task TriggerCheckNowAsync(int requestId)
    {
        if (ShouldFail)
        {
            throw new HttpRequestException("simulated GitHub failure");
        }

        CheckedRequestIds.Add(requestId);
        return Task.CompletedTask;
    }
}

// Boots the real API (real pipeline, controllers, rate limiter) with an
// in-memory database and a fake GitHub dispatcher swapped in.
public sealed class ApiFactory(Dictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    public FakeDispatchService Dispatch { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(settings ?? []));

        builder.ConfigureServices(services =>
        {
            // Drop everything registered for AppDbContext (options, the Npgsql
            // configuration, the context itself) - two providers can't coexist.
            var appDbContextServices = services
                .Where(d => d.ServiceType == typeof(AppDbContext)
                    || d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().SequenceEqual([typeof(AppDbContext)])))
                .ToList();
            foreach (var descriptor in appDbContextServices)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(GitHubDispatchService)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<GitHubDispatchService>(Dispatch);
        });
    }

    public void Seed(Action<AppDbContext> seed)
    {
        using var scope = Services.CreateScope();
        seed(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public T Query<T>(Func<AppDbContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public HttpClient CreateClientFrom(string ip)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return client;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
