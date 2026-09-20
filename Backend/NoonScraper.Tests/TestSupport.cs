using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MysticMind.PostgresEmbed;
using Npgsql;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Tests;

// Every test that touches the database runs against real PostgreSQL - the same
// engine as production. EF Core's in-memory provider was used before, and it
// doesn't enforce unique indexes, partial indexes, foreign keys, advisory locks,
// or Postgres's ordering and null semantics, which are exactly the things the
// concurrency and integrity tests are about.
//
// One server per test run: by default an embedded PostgreSQL that is downloaded
// once and started on a free port; set NOON_TEST_POSTGRES to a connection string
// (with rights to CREATE DATABASE) to use an existing server instead - CI does.
// The migrations are applied once to a template database, and each test gets its
// own database cloned from it, so tests are isolated and can run in parallel.
public static class TestPostgres
{
    private sealed record Host(string AdminConnectionString, Func<string, string> ConnectionFor);

    private static readonly Lazy<Task<Host>> Server = new(StartAsync);

    private static readonly SemaphoreSlim CreateLock = new(1, 1);

    public static async Task<string> CreateDatabaseAsync()
    {
        var host = await Server.Value;

        // CREATE DATABASE ... TEMPLATE needs the template to have no other
        // sessions and is slow under contention, so creations are serialized.
        await CreateLock.WaitAsync();
        try
        {
            var name = "t_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(host.AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE noon_template", connection);
            await command.ExecuteNonQueryAsync();
            return host.ConnectionFor(name);
        }
        finally
        {
            CreateLock.Release();
        }
    }

    // A database with no schema at all - for tests that apply migrations themselves.
    public static async Task<string> CreateEmptyDatabaseAsync()
    {
        var host = await Server.Value;
        var name = "e_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(host.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return host.ConnectionFor(name);
    }

    private static async Task<Host> StartAsync()
    {
        Host host;

        var external = Environment.GetEnvironmentVariable("NOON_TEST_POSTGRES");
        if (!string.IsNullOrEmpty(external))
        {
            var builder = new NpgsqlConnectionStringBuilder(external) { Pooling = false };
            host = new Host(builder.ConnectionString, database =>
                new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = database }.ConnectionString);
        }
        else
        {
            var port = FreePort();
            var server = new PgServer(
                "17.2.0",
                port: port,
                // Throwaway data: durability only costs time. Fewer fsyncs is what keeps
                // CREATE DATABASE (one per test) and the commit-heavy race tests fast.
                pgServerParams: new Dictionary<string, string>
                {
                    ["max_connections"] = "400",
                    ["fsync"] = "off",
                    ["synchronous_commit"] = "off",
                    ["full_page_writes"] = "off",
                    // The tests raise unique/FK violations on purpose, and Postgres logs
                    // every one. The server's stderr is a pipe nothing reads, so once
                    // enough log has piled up its buffer fills and the next statement that
                    // logs blocks forever - which showed up as 30-second command timeouts
                    // in exactly the tests that provoke errors, and only in a full run.
                    ["log_min_messages"] = "panic",
                    ["log_min_error_statement"] = "panic"
                },
                clearInstanceDirOnStop: true);
            await server.StartAsync();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Stop();

            string For(string database) =>
                $"Host=localhost;Port={port};Username=postgres;Password=test;Database={database};Pooling=false";
            host = new Host(For("postgres"), For);
        }

        await using (var connection = new NpgsqlConnection(host.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS noon_template", connection);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE noon_template", connection);
            await create.ExecuteNonQueryAsync();
        }

        // The migrations themselves are what builds the schema, so every test
        // also exercises them (and MigrationTests checks they match the model).
        await using (var template = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(host.ConnectionFor("noon_template")).Options))
        {
            await template.Database.MigrateAsync();
        }

        NpgsqlConnection.ClearAllPools();
        return host;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public static class TestDb
{
    // A fresh, migrated database, and a context on it.
    public static AppDbContext Create() => Open(TestPostgres.CreateDatabaseAsync().GetAwaiter().GetResult());

    public static string CreateConnectionString() => TestPostgres.CreateDatabaseAsync().GetAwaiter().GetResult();

    // Another context (its own connection) on an existing test database - what two
    // concurrent processes look like.
    public static AppDbContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    public static Product AddProduct(
        AppDbContext db,
        string url = "https://www.noon.com/egypt-en/some-product/N1/p/",
        string? name = "Some Product",
        Category? category = null,
        ProductSource source = ProductSource.Seed,
        bool isActive = true)
    {
        var product = new Product { Url = url, Name = name, Category = category, Source = source, IsActive = isActive };
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

// Counts the SQL commands the application runs, so a test can assert that an
// endpoint's query count doesn't grow with the amount of data (no N+1).
public sealed class DbCommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override System.Data.Common.DbDataReader ReaderExecuted(
        System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
        System.Data.Common.DbDataReader result)
    {
        Interlocked.Increment(ref _count);
        return result;
    }

    public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
        System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
        System.Data.Common.DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return ValueTask.FromResult(result);
    }
}

public sealed class FakeJobDispatcher : IJobDispatcher
{
    private readonly object _gate = new();

    public List<(string Kind, int RequestId, int ProductId, Guid CorrelationId)> Dispatched { get; } = [];

    public bool ShouldFail { get; set; }

    public Exception? FailureToThrow { get; set; }

    public IEnumerable<int> CrawledProductIds => Dispatched.Where(d => d.Kind == "crawl-product").Select(d => d.ProductId);

    public IEnumerable<int> CheckedRequestIds => Dispatched.Where(d => d.Kind == "check-now").Select(d => d.RequestId);

    public Task DispatchCrawlProductAsync(ProductCrawlRequest request, CancellationToken ct = default) =>
        Record("crawl-product", request);

    public Task DispatchCheckNowAsync(CheckNowRequest request, CancellationToken ct = default) =>
        Record("check-now", request);

    private Task Record(string kind, JobRequestBase request)
    {
        if (ShouldFail || FailureToThrow is not null)
        {
            throw FailureToThrow ?? new DispatchException("GitHub rejected the dispatch (HTTP 503).");
        }

        lock (_gate)
        {
            Dispatched.Add((kind, request.Id, request.ProductId, request.CorrelationId));
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeTelegramSender : ITelegramSender
{
    private readonly object _gate = new();

    public bool IsConfigured { get; set; } = true;

    public List<(long ChatId, string Text)> Sent { get; } = [];

    // What the next sends return; defaults to success.
    public Func<long, SendOutcome> Outcome { get; set; } = _ => SendOutcome.Sent;

    public Task<SendOutcome> SendAsync(long chatId, string text, CancellationToken ct = default)
    {
        var outcome = Outcome(chatId);
        if (outcome == SendOutcome.Sent)
        {
            lock (_gate)
            {
                Sent.Add((chatId, text));
            }
        }

        return Task.FromResult(outcome);
    }
}

// Boots the real API (real pipeline, controllers, rate limiter, exception handler)
// on its own real PostgreSQL database, with the two things that leave the process
// - GitHub dispatch and Telegram - replaced by fakes, and a controllable clock.
public sealed class ApiFactory(
    Dictionary<string, string?>? settings = null,
    string environment = "Development",
    string? sharedConnectionString = null,
    DateTimeOffset? startTime = null)
    : WebApplicationFactory<Program>
{
    // Pass an existing test database's connection string to get a second app
    // instance on the same data - what a container restart or a second replica is.
    //
    // Lazy and thread-safe on purpose: the first access can come from many
    // concurrent requests at once, and an unsynchronized `??=` there created a
    // separate database per request - which made every concurrency test through
    // the API pass or fail for the wrong reason (each request saw an empty DB).
    private readonly Lazy<string> _connectionString = new(
        () => sharedConnectionString ?? TestDb.CreateConnectionString(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public FakeJobDispatcher Dispatch { get; } = new();

    public FakeTelegramSender Telegram { get; } = new();

    public FakeTimeProvider Clock { get; } = new(startTime ?? DateTimeOffset.UtcNow);

    public DbCommandCounter Commands { get; } = new();

    public string ConnectionString => _connectionString.Value;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // Developer machines keep real secrets in user-secrets, which the app loads in
        // Development. Blank them first so no test depends on (or could ever use)
        // what happens to be on this machine; a test that needs a value passes it.
        var effective = new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "",
            ["Telegram:WebhookSecret"] = "",
            ["GitHubDispatch:Token"] = ""
        };
        foreach (var (key, value) in settings ?? [])
        {
            effective[key] = value;
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(effective));

        builder.ConfigureServices(services =>
        {
            // Swap the production database registration (Npgsql to Neon) for one on
            // this test's own database. Everything registered for AppDbContext is
            // removed first - two configurations can't both apply.
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

            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(ConnectionString).AddInterceptors(Commands));

            Replace<IJobDispatcher>(services, Dispatch);
            Replace<ITelegramSender>(services, Telegram);
            Replace<TimeProvider>(services, Clock);
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(instance);
    }

    public void Seed(Action<AppDbContext> seed)
    {
        using var db = TestDb.Open(ConnectionString);
        seed(db);
    }

    public T Query<T>(Func<AppDbContext, T> query)
    {
        using var db = TestDb.Open(ConnectionString);
        return query(db);
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
