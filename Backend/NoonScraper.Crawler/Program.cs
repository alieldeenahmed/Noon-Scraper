using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NoonScraper.Crawler;
using NoonScraper.Data;
using NoonScraper.Data.Models;

// Passthrough to Playwright's own CLI (`install`/`install-deps`), since Linux
// environments (this project's WSL dev setup, and the GitHub Actions runner)
// have no PowerShell to run the generated playwright.ps1 script. Playwright.ps1
// itself does nothing but load Microsoft.Playwright.dll and call this same
// method, so calling it directly needs no extra tooling.
if (args.Length > 0 && (args[0] == "install" || args[0] == "install-deps"))
{
    Environment.Exit(Microsoft.Playwright.Program.Main(args));
}

var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder only auto-loads user secrets when the environment
// is "Development", and a bare console app has no launchSettings.json to set that -
// so it's added explicitly here regardless of environment. In CI, the connection
// string instead comes from an environment variable (ConnectionStrings__DefaultConnection),
// which the default configuration sources already pick up.
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient();

using var host = builder.Build();

// Invoked by the check-now.yml GitHub Actions workflow, triggered by the API
// via a repository_dispatch event - the API can no longer launch Chrome itself,
// so this is the one place that actually performs an on-demand check.
if (args.Length > 0 && args[0] == "check-now")
{
    var requestId = int.Parse(args[1]);

    using var checkScope = host.Services.CreateScope();
    var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();

    var request = await checkDb.CheckNowRequests
        .Include(r => r.Product)
        .FirstOrDefaultAsync(r => r.Id == requestId);

    if (request is null)
    {
        Console.WriteLine($"CheckNowRequest {requestId} not found.");
        Environment.Exit(1);
    }

    try
    {
        await using var checkBrowser = await StealthBrowser.LaunchAsync();
        var offers = await OfferScraper.ScrapeOffersAsync(checkBrowser.Page, request!.Product!.Url);

        if (offers.Count == 0)
        {
            throw new InvalidOperationException("No offers found on the product page.");
        }

        request.Status = CheckNowStatus.Completed;
        request.ResultJson = JsonSerializer.Serialize(offers.OrderBy(o => o.Price));
        Console.WriteLine($"Completed check-now for request {requestId}: {offers.Count} offer(s)");
    }
    catch (Exception ex)
    {
        request!.Status = CheckNowStatus.Failed;
        request.ErrorMessage = ex.Message;
        Console.WriteLine($"Failed check-now for request {requestId}: {ex.Message}");
    }

    request!.CompletedAt = DateTimeOffset.UtcNow;
    await checkDb.SaveChangesAsync();
    Environment.Exit(0);
}

// Invoked by the crawl-product.yml workflow right after a user submits a URL
// via POST /products - without this, a freshly-added product would just sit
// as a bare record (no name, price, or stock) until the next scheduled daily
// crawl reached it, up to 24 hours later.
if (args.Length > 0 && args[0] == "crawl-product")
{
    var crawlProductId = int.Parse(args[1]);

    using var crawlScope = host.Services.CreateScope();
    var crawlDb = crawlScope.ServiceProvider.GetRequiredService<AppDbContext>();
    var crawlHttpClient = crawlScope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
    var crawlTelegramToken = builder.Configuration["Telegram:BotToken"] ?? builder.Configuration["TELEGRAM_BOT_TOKEN"];

    var targetProduct = await crawlDb.Products.FindAsync(crawlProductId);
    if (targetProduct is null)
    {
        Console.WriteLine($"Product {crawlProductId} not found.");
        Environment.Exit(1);
    }

    try
    {
        await using var crawlBrowser = await StealthBrowser.LaunchAsync();
        var scrapedProduct = await ProductPageScraper.ScrapeAsync(crawlBrowser.Page, targetProduct!.Url);

        if (scrapedProduct is null)
        {
            throw new InvalidOperationException("No Product data found at that URL.");
        }

        await ProductUpserter.UpsertAsync(
            crawlDb, crawlHttpClient, crawlTelegramToken, [], scrapedProduct, ProductSource.UserAdded, category: null);
        await crawlDb.SaveChangesAsync();
        Console.WriteLine($"Crawled product {crawlProductId}: {scrapedProduct.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to crawl product {crawlProductId}: {ex.Message}");
        Environment.Exit(1);
    }

    Environment.Exit(0);
}

var categoryUrls = new (Category Category, string Url)[]
{
    (Category.Mobiles, "https://www.noon.com/egypt-en/mobiles/"),
    (Category.Laptops, "https://www.noon.com/egypt-en/electronics-and-mobiles/computers-and-accessories/computers-new/laptops/"),
    (Category.SkinCare, "https://www.noon.com/egypt-en/eg-skin-care/"),
    (Category.HairCare, "https://www.noon.com/egypt-en/eg-hair-care/"),
    (Category.PersonalCare, "https://www.noon.com/egypt-en/eg-personal-care/")
};

await using var stealthBrowser = await StealthBrowser.LaunchAsync();
var page = stealthBrowser.Page;

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();

// Same flat-variable fallback as the connection string - Back4app's
// environment-variable UI rejects "__" hierarchical naming, so wherever this
// ends up configured outside local user secrets, it'll need a flat name.
var telegramBotToken = builder.Configuration["Telegram:BotToken"] ?? builder.Configuration["TELEGRAM_BOT_TOKEN"];

// Tracks products already added in this run, since the same product can appear
// in multiple widgets on one page (e.g. both "Best sellers" and "Shop all mobiles"),
// and neither would be found by a DB lookup until SaveChangesAsync actually runs.
var productsInThisRun = new Dictionary<string, Product>();

foreach (var (category, url) in categoryUrls)
{
    Console.WriteLine($"Scraping {category} at {url}");

    List<ScrapedProduct> scraped;
    try
    {
        scraped = await CategoryScraper.ScrapeAsync(page, url);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Failed to scrape {category}: {ex.Message}");
        continue;
    }

    Console.WriteLine($"  Found {scraped.Count} products");

    foreach (var item in scraped)
    {
        // Skip products already handled in this run - the same product can appear
        // in multiple widgets on one page, and a second snapshot from the same
        // crawl would just be redundant, near-identical history.
        if (productsInThisRun.ContainsKey(item.Url))
        {
            continue;
        }

        await ProductUpserter.UpsertAsync(db, httpClient, telegramBotToken, productsInThisRun, item, ProductSource.Seed, category);
    }

    await db.SaveChangesAsync();
}

// User-submitted products aren't covered by any of the 5 tracked category pages,
// so they're kept up to date via their own detail page instead, every crawl.
var userAddedProducts = await db.Products
    .Where(p => p.Source == ProductSource.UserAdded && p.IsActive)
    .ToListAsync();

Console.WriteLine($"Scraping {userAddedProducts.Count} user-added product(s)");

foreach (var product in userAddedProducts)
{
    ScrapedProduct? scraped;
    try
    {
        scraped = await ProductPageScraper.ScrapeAsync(page, product.Url);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Failed to scrape {product.Url}: {ex.Message}");
        continue;
    }

    if (scraped is null)
    {
        Console.WriteLine($"  No Product data found at {product.Url}");
        continue;
    }

    await ProductUpserter.UpsertAsync(db, httpClient, telegramBotToken, productsInThisRun, scraped, ProductSource.UserAdded, category: null);
    await db.SaveChangesAsync();
}

Console.WriteLine("Done.");
