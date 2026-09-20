using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoonScraper.Crawler;
using NoonScraper.Crawler.Jobs;
using NoonScraper.Data;
using NoonScraper.Data.Configuration;
using NoonScraper.Data.Notifications;

// Usage:
//   NoonScraper.Crawler                                    the scheduled crawl
//   NoonScraper.Crawler crawl-product <requestId>          crawl one submitted product
//   NoonScraper.Crawler check-now <requestId>              cross-merchant comparison
//   NoonScraper.Crawler fail-request <kind> <id> [reason]  close a request whose workflow died
//   NoonScraper.Crawler install | install-deps             Playwright's own installer
//
// Anything else is a usage error. (It used to fall through to a full crawl, which
// made a mistyped flag - or `--help` - quietly start scraping the live site and
// writing to the database.)
const int UsageError = 64;

// Passthrough to Playwright's own CLI (`install`/`install-deps`), since Linux
// environments (this project's WSL dev setup, and the GitHub Actions runner)
// have no PowerShell to run the generated playwright.ps1 script.
if (args.Length > 0 && (args[0] == "install" || args[0] == "install-deps"))
{
    return Microsoft.Playwright.Program.Main(args);
}

var mode = args.FirstOrDefault();
var validUsage = mode switch
{
    null => args.Length == 0,
    "crawl-product" or "check-now" => args.Length == 2 && int.TryParse(args[1], out _),
    "fail-request" => args.Length is 3 or 4 && int.TryParse(args[2], out _),
    _ => false
};

if (!validUsage)
{
    Console.Error.WriteLine("Usage: NoonScraper.Crawler [crawl-product|check-now <requestId>] | [fail-request <kind> <id> [reason]]");
    return UsageError;
}

var builder = Host.CreateApplicationBuilder([]);

// Host.CreateApplicationBuilder only auto-loads user secrets when the environment
// is "Development", and a bare console app has no launchSettings.json to set that -
// so it's added explicitly here regardless of environment. In CI, the connection
// string instead comes from an environment variable (ConnectionStrings__DefaultConnection),
// which the default configuration sources already pick up.
builder.Configuration.AddUserSecrets<Program>();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Telegram's API puts the bot token in the URL path, and the HTTP client's own
// logging prints request URLs. Actions masks registered secrets, but the token
// shouldn't be in a log line at all.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var connectionString = builder.Configuration.GetDatabaseConnectionString();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));

builder.Services.Configure<CrawlOptions>(builder.Configuration.GetSection(CrawlOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<ITelegramSender>(sp => new TelegramSender(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram"),
    builder.Configuration.GetTelegramBotToken(),
    sp.GetRequiredService<ILogger<TelegramSender>>()));

builder.Services.AddSingleton<IScrapeSessionFactory, BrowserScrapeSessionFactory>();
builder.Services.AddScoped<SubscriptionNotifier>();
builder.Services.AddScoped<ProductRecorder>();
builder.Services.AddScoped<DailyCrawl>();
builder.Services.AddScoped<CrawlProductJob>();
builder.Services.AddScoped<CheckNowJob>();

using var cancellation = new CancellationTokenSource();

// GitHub cancels a run (or hits its timeout) with SIGINT, then SIGTERM. Turning
// that into a token lets the job record that it was cancelled instead of dying
// with its request stuck in Running.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
});

var gitHubRunId = long.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), out var parsedRunId)
    ? parsedRunId
    : (long?)null;

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var services = scope.ServiceProvider;
var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("NoonScraper.Crawler");

try
{
    switch (mode)
    {
        case "crawl-product":
            return Report(
                await services.GetRequiredService<CrawlProductJob>().RunAsync(int.Parse(args[1]), gitHubRunId, cancellation.Token),
                "Product crawl");

        case "check-now":
            return Report(
                await services.GetRequiredService<CheckNowJob>().RunAsync(int.Parse(args[1]), gitHubRunId, cancellation.Token),
                "Cross-merchant check");

        case "fail-request":
            return await FailRequestAsync(services, args);

        default:
            var options = services.GetRequiredService<IOptions<CrawlOptions>>().Value;
            var summary = await services.GetRequiredService<DailyCrawl>().RunAsync(gitHubRunId, cancellation.Token);
            if (summary.SkippedBecauseAnotherRunIsActive)
            {
                return ExitCodes.Success;
            }

            var code = summary.ExitCode(options);
            if (code == ExitCodes.Success && summary.ProductsFailed > 0)
            {
                Console.WriteLine($"::warning title=Daily crawl::{summary}");
            }

            return Report(code, "Daily crawl", summary.ToString());
    }
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Unhandled failure");
    Console.WriteLine($"::error title=Crawler crashed::{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    return ExitCodes.Failure;
}

// GitHub Actions turns "::error"/"::warning" lines into annotations on the run,
// so a failure is visible on the run's summary page without opening the log.
static int Report(int exitCode, string what, string? detail = null)
{
    if (exitCode == ExitCodes.Failure)
    {
        Console.WriteLine($"::error title={what} failed::{detail ?? "see the log for the failing stage"}");
    }

    return exitCode;
}

// Closes a request whose workflow died before the crawler could (a failed Chrome
// install, say). Only affects a request that is still Pending or Running.
static async Task<int> FailRequestAsync(IServiceProvider services, string[] args)
{
    var db = services.GetRequiredService<AppDbContext>();
    var clock = services.GetRequiredService<TimeProvider>();
    var id = int.Parse(args[2]);
    var reason = args.Length == 4 ? args[3] : "The workflow run failed before the crawler reported a result.";

    var failed = args[1] switch
    {
        "crawl-product" => await JobLifecycle.TryFailAsync(db.ProductCrawlRequests, id, "workflow", reason, clock.GetUtcNow()),
        "check-now" => await JobLifecycle.TryFailAsync(db.CheckNowRequests, id, "workflow", reason, clock.GetUtcNow()),
        _ => throw new ArgumentException($"Unknown request kind '{args[1]}'.")
    };

    Console.WriteLine(failed
        ? $"Marked {args[1]} request {id} as failed."
        : $"{args[1]} request {id} was already closed; nothing to do.");
    return ExitCodes.Success;
}
