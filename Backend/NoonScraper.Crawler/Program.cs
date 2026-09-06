using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
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

using var host = builder.Build();

var categoryUrls = new (Category Category, string Url)[]
{
    (Category.Mobiles, "https://www.noon.com/egypt-en/mobiles/"),
    (Category.Laptops, "https://www.noon.com/egypt-en/electronics-and-mobiles/computers-and-accessories/computers-new/laptops/"),
    (Category.SkinCare, "https://www.noon.com/egypt-en/eg-skin-care/"),
    (Category.HairCare, "https://www.noon.com/egypt-en/eg-hair-care/"),
    (Category.PersonalCare, "https://www.noon.com/egypt-en/eg-personal-care/")
};

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Channel = "chrome",
    Headless = false,
    Args = ["--disable-blink-features=AutomationControlled"],
    IgnoreDefaultArgs = ["--enable-automation"]
});

var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    Locale = "en-US",
    TimezoneId = "Africa/Cairo",
    ViewportSize = new ViewportSize { Width = 1366, Height = 768 }
});

// Masks the cheap, static automation signals (navigator.webdriver, missing chrome
// runtime, plugin/language fingerprints) that bot-detection scripts check before
// any real interaction happens. Does not defeat behavioral analysis.
await context.AddInitScriptAsync("""
    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

    window.chrome = { runtime: {} };

    const originalQuery = window.navigator.permissions.query;
    window.navigator.permissions.query = (parameters) => (
        parameters.name === 'notifications'
            ? Promise.resolve({ state: Notification.permission })
            : originalQuery(parameters)
    );

    Object.defineProperty(navigator, 'plugins', {
        get: () => [1, 2, 3, 4, 5].map(() => ({ name: 'Chrome PDF Plugin' }))
    });

    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
    """);

var page = await context.NewPageAsync();

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

// Tracks products already added in this run, since the same product can appear
// in multiple widgets on one page (e.g. both "Best sellers" and "Shop all mobiles"),
// and neither would be found by a DB lookup until SaveChangesAsync actually runs.
var productsInThisRun = new Dictionary<string, Product>();

async Task<Product> UpsertAsync(ScrapedProduct item, ProductSource source, Category? category)
{
    if (!productsInThisRun.TryGetValue(item.Url, out var product))
    {
        product = await db.Products.FirstOrDefaultAsync(p => p.Url == item.Url);
        if (product is null)
        {
            product = new Product
            {
                Url = item.Url,
                Source = source,
                IsActive = true
            };
            db.Products.Add(product);
        }

        productsInThisRun[item.Url] = product;
    }

    product.NoonProductId = item.NoonProductId;
    product.Name = item.Name;
    product.Rating = item.Rating;
    if (category is not null)
    {
        product.Category = category;
    }
    if (item.MerchantName is not null)
    {
        product.MerchantName = item.MerchantName;
    }

    var isRestock = await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, item.Stock);

    var newSnapshot = new PriceSnapshot
    {
        ProductId = product.Id,
        Product = product,
        Price = item.Price,
        Stock = item.Stock,
        DiscountPercent = item.DiscountPercent
    };
    db.PriceSnapshots.Add(newSnapshot);

    if (isRestock)
    {
        Console.WriteLine($"  RESTOCK: {product.Name ?? product.Url}");
    }

    var fakeDiscount = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(
        db, product, newSnapshot, item.Price, item.DiscountPercent);
    if (fakeDiscount is not null)
    {
        db.DiscountFlags.Add(fakeDiscount);
        Console.WriteLine(
            $"  SUSPICIOUS DISCOUNT: {product.Name ?? product.Url} - claims {item.DiscountPercent}% off, " +
            $"but {fakeDiscount.DiscountedPrice} doesn't beat the historical low");
    }

    return product;
}

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

        await UpsertAsync(item, ProductSource.Seed, category);
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

    await UpsertAsync(scraped, ProductSource.UserAdded, category: null);
    await db.SaveChangesAsync();
}

Console.WriteLine("Done.");
