using System.Text.Json.Serialization;
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors(FrontendCorsPolicy);

app.UseAuthorization();

app.MapControllers();

app.Run();
