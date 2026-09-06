using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using NoonScraper.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

using var host = builder.Build();

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

page.Response += (_, res) =>
{
    if (!res.Ok)
    {
        Console.WriteLine($"FAILED RESPONSE: {res.Status} {res.Url}");
    }
};

var response = await page.GotoAsync("https://www.noon.com/egypt-en/mobiles/");

Console.WriteLine($"HTTP status: {response?.Status}");
Console.WriteLine($"Page title: {await page.TitleAsync()}");
Console.WriteLine($"URL after navigation: {page.Url}");

await page.WaitForTimeoutAsync(8000);

Console.WriteLine($"Page title after wait: {await page.TitleAsync()}");

var content = await page.ContentAsync();
Console.WriteLine($"HTML length after wait: {content.Length}");
await File.WriteAllTextAsync(
    @"C:\Users\aliel\AppData\Local\Temp\claude\G--Code-projects-Noon-Scraper-Backend\bd89ba35-1548-40aa-ae3c-d00ecc20bcc4\scratchpad\noon-page.html",
    content);

await page.ScreenshotAsync(new PageScreenshotOptions
{
    Path = @"C:\Users\aliel\AppData\Local\Temp\claude\G--Code-projects-Noon-Scraper-Backend\bd89ba35-1548-40aa-ae3c-d00ecc20bcc4\scratchpad\noon-smoke-test.png",
    FullPage = true
});
