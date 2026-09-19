using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Services;
using NoonScraper.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

const string FrontendCorsPolicy = "FrontendCorsPolicy";

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Back4app's environment-variable UI rejects the "__" hierarchical naming
// .NET normally uses for ConnectionStrings__DefaultConnection, so it's set
// there as a flat DATABASE variable instead - fall back to that.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["DATABASE"];
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddHttpClient<GitHubDispatchService>();
builder.Services.AddHttpClient<TelegramService>();

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
// The proxy chain's depth isn't known here, so the whole X-Forwarded-For
// chain is honored (ForwardLimit = null) and the client's address is taken
// from the front of it. That header is client-forgeable, which is why the
// dispatch endpoints also carry a combined cap that ignores IPs entirely.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = null;
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
