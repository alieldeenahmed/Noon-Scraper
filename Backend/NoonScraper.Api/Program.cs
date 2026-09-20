using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Configuration;
using NoonScraper.Data.Notifications;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

const string FrontendCorsPolicy = "FrontendCorsPolicy";

// Every endpoint takes a few bytes of JSON at most; the default 30MB request limit
// only helps someone trying to make the server buffer garbage.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

// Single-line console output with scopes, so the request/product/correlation ids
// attached by ASP.NET and by the code below appear on every line they apply to.
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.IncludeScopes = true;
    console.TimestampFormat = "HH:mm:ss ";
});

// The Telegram API URL contains the bot token, and the HTTP client's own logging
// prints request URLs - keep it out of the logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Unhandled exceptions become an RFC 7807 problem response carrying a trace id
// (never the exception text), and the exception itself is logged with that id.
builder.Services.AddProblemDetails();

// Back4app's environment-variable UI rejects the "__" hierarchical naming
// .NET normally uses for ConnectionStrings__DefaultConnection, so it's set
// there as a flat DATABASE variable instead - GetDatabaseConnectionString
// handles both. Transient connection failures (a Neon database waking from
// suspend) are retried.
var connectionString = builder.Configuration.GetDatabaseConnectionString();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<JobOptions>(builder.Configuration.GetSection(JobOptions.SectionName));

builder.Services.AddHttpClient<IJobDispatcher, GitHubDispatchService>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<JobRequestService>();

builder.Services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<ITelegramSender>(sp => new TelegramSender(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram"),
    sp.GetRequiredService<IConfiguration>().GetTelegramBotToken(),
    sp.GetRequiredService<ILogger<TelegramSender>>()));

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod();

        if (builder.Environment.IsDevelopment())
        {
            // Vite bumps to the next free port whenever something else on the
            // machine already holds 5173 (e.g. another local project already
            // running) - trust any localhost origin in dev rather than
            // hardcoding one port and breaking every time that happens.
            policy.SetIsOriginAllowed(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
        }
        else
        {
            policy.WithOrigins("https://noon-scraper-phi.vercel.app");
        }
    });
});

builder.Services.AddApiRateLimiting();

// Behind the hosting platform's proxies the socket peer is a proxy, not the
// client - without this every visitor would share one rate-limit bucket.
//
// How many proxies sit in front isn't known here, so by default the whole
// X-Forwarded-For chain is honored and the client's address is taken from the
// front of it. That header is client-forgeable: a caller can claim any address.
// Network:ForwardLimit (an integer) pins the number of trusted proxy hops once
// it's known for the hosting platform. Until then nothing important rests on the
// client IP - the limits that guard GitHub Actions spend and the crawl workload
// are counted in the database, and the in-process limiter has IP-independent
// combined caps (see RateLimiting.cs).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = builder.Configuration.GetValue<int?>("Network:ForwardLimit");
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();

app.UseExceptionHandler();

app.UseHttpsRedirection();

app.UseCors(FrontendCorsPolicy);

// After CORS, so a 429 still carries the CORS headers the browser needs to
// let the frontend read it.
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposes the entry point to the integration tests' WebApplicationFactory.
public partial class Program;
