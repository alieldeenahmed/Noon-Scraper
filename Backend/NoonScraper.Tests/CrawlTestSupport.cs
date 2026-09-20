using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NoonScraper.Crawler;
using NoonScraper.Crawler.Jobs;
using NoonScraper.Data;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Tests;

// A browser that does what the test says. The jobs only see IScrapeSession, so
// their failure handling can be driven without Chrome.
public sealed class FakeScrapeSession : IScrapeSession
{
    private readonly ConcurrentQueue<string> _calls = new();
    private int _resets;

    public IReadOnlyCollection<string> Calls => _calls;

    public int ResetCount => Volatile.Read(ref _resets);

    public Func<string, CancellationToken, Task<CategoryScrapeResult>> Category { get; set; } =
        (_, _) => Task.FromResult(new CategoryScrapeResult([], []));

    public Func<string, CancellationToken, Task<ScrapedProduct?>> Product { get; set; } =
        (_, _) => Task.FromResult<ScrapedProduct?>(null);

    public Func<string, CancellationToken, Task<IReadOnlyList<OfferResult>>> Offers { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<OfferResult>>([]);

    public Task<CategoryScrapeResult> ScrapeCategoryAsync(string url, CancellationToken ct)
    {
        _calls.Enqueue("category:" + url);
        return Category(url, ct);
    }

    public Task<ScrapedProduct?> ScrapeProductAsync(string url, CancellationToken ct)
    {
        _calls.Enqueue("product:" + url);
        return Product(url, ct);
    }

    public Task<IReadOnlyList<OfferResult>> ScrapeOffersAsync(string url, CancellationToken ct)
    {
        _calls.Enqueue("offers:" + url);
        return Offers(url, ct);
    }

    public Task ResetAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _resets);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public int CallsTo(string kind) => _calls.Count(c => c.StartsWith(kind + ":"));
}

public sealed class FakeScrapeSessionFactory(FakeScrapeSession session) : IScrapeSessionFactory
{
    public int OpenCount { get; private set; }

    public Exception? OpenFailure { get; set; }

    public Task<IScrapeSession> OpenAsync(CancellationToken ct)
    {
        OpenCount++;
        return OpenFailure is null ? Task.FromResult<IScrapeSession>(session) : Task.FromException<IScrapeSession>(OpenFailure);
    }
}

// Captures log lines together with the scopes active when they were written, so
// tests can assert that correlation ids really are attached.
public sealed class CapturingLogger<T> : ILogger<T>
{
    private static readonly AsyncLocal<ScopeNode?> Current = new();

    private sealed record ScopeNode(object? State, ScopeNode? Parent);

    public ConcurrentQueue<LoggedLine> Lines { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var parent = Current.Value;
        Current.Value = new ScopeNode(state, parent);
        return new Restore(parent);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var scope = new Dictionary<string, object?>();
        for (var node = Current.Value; node is not null; node = node.Parent)
        {
            if (node.State is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    scope.TryAdd(key, value);
                }
            }
        }

        Lines.Enqueue(new LoggedLine(logLevel, formatter(state, exception), scope, exception));
    }

    private sealed class Restore(ScopeNode? parent) : IDisposable
    {
        public void Dispose() => Current.Value = parent;
    }
}

public sealed record LoggedLine(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Scope, Exception? Exception);

// Wires up the crawl-side services on a real database, with fakes for what leaves
// the process (the browser, Telegram) and a controllable clock. Each Db()/service
// call can take its own context, which is how two concurrent processes are modelled.
public sealed class CrawlHarness : IDisposable
{
    private readonly List<AppDbContext> _contexts = [];

    public CrawlHarness()
    {
        ConnectionString = TestDb.CreateConnectionString();
        Session = new FakeScrapeSession();
        Sessions = new FakeScrapeSessionFactory(Session);
        Options = new CrawlOptions { RetryDelay = TimeSpan.Zero };
        Db = NewContext();
    }

    public string ConnectionString { get; }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    public FakeTelegramSender Telegram { get; } = new();

    public FakeScrapeSession Session { get; }

    public FakeScrapeSessionFactory Sessions { get; }

    public CrawlOptions Options { get; }

    public CapturingLogger<CheckNowJob> CheckNowLog { get; } = new();

    public CapturingLogger<CrawlProductJob> CrawlProductLog { get; } = new();

    public AppDbContext NewContext()
    {
        var db = TestDb.Open(ConnectionString);
        _contexts.Add(db);
        return db;
    }

    public SubscriptionNotifier NewNotifier(AppDbContext? db = null, ITelegramSender? sender = null) =>
        new(db ?? Db, sender ?? Telegram, Clock, NullLogger<SubscriptionNotifier>.Instance);

    public ProductRecorder NewRecorder(AppDbContext? db = null, ITelegramSender? sender = null)
    {
        db ??= Db;
        return new ProductRecorder(db, NewNotifier(db, sender), Clock, NullLogger<ProductRecorder>.Instance);
    }

    public CheckNowJob NewCheckNowJob(AppDbContext? db = null) =>
        new(db ?? NewContext(), Sessions, Clock, Options.AsOptions(), CheckNowLog);

    public CrawlProductJob NewCrawlProductJob(AppDbContext? db = null)
    {
        db ??= NewContext();
        return new CrawlProductJob(db, Sessions, NewRecorder(db), Clock, Options.AsOptions(), CrawlProductLog);
    }

    public DailyCrawl NewDailyCrawl(AppDbContext? db = null)
    {
        db ??= NewContext();
        return new DailyCrawl(db, Sessions, NewRecorder(db), Clock, Options.AsOptions(), NullLogger<DailyCrawl>.Instance);
    }

    public void Dispose()
    {
        foreach (var db in _contexts)
        {
            db.Dispose();
        }
    }

    // ---- data builders ----

    public Product AddProduct(string sku = "N1", ProductSource source = ProductSource.UserAdded, bool active = true, string? name = null) =>
        TestDb.AddProduct(Db, $"https://www.noon.com/egypt-en/item-{sku.ToLowerInvariant()}/{sku}/p/", name, null, source, active);

    public CheckNowRequest AddCheckNow(Product product, JobStatus status = JobStatus.Pending, DateTimeOffset? requestedAt = null)
    {
        var request = new CheckNowRequest
        {
            ProductId = product.Id,
            Status = status,
            RequestedAt = requestedAt ?? Clock.GetUtcNow()
        };
        Db.CheckNowRequests.Add(request);
        Db.SaveChanges();
        return request;
    }

    public ProductCrawlRequest AddCrawl(Product product, JobStatus status = JobStatus.Pending, DateTimeOffset? requestedAt = null)
    {
        var request = new ProductCrawlRequest
        {
            ProductId = product.Id,
            Status = status,
            RequestedAt = requestedAt ?? Clock.GetUtcNow()
        };
        Db.ProductCrawlRequests.Add(request);
        Db.SaveChanges();
        return request;
    }

    public static ScrapedProduct Scraped(string sku, decimal price = 100, bool stock = true, decimal? discount = null) => new()
    {
        NoonProductId = sku,
        Url = $"https://www.noon.com/egypt-en/item-{sku.ToLowerInvariant()}/{sku}/p/",
        Name = $"Item {sku}",
        Price = price,
        Stock = stock,
        DiscountPercent = discount,
        MerchantName = "noon",
        Rating = 4.5m
    };

    // Read through a fresh context, so what's seen is what is committed - not what
    // this context happens to have cached.
    public T Query<T>(Func<AppDbContext, T> query)
    {
        using var fresh = TestDb.Open(ConnectionString);
        return query(fresh);
    }
}

public static class OptionsExtensions
{
    public static IOptions<CrawlOptions> AsOptions(this CrawlOptions options) => Options.Create(options);
}
