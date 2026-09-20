using Microsoft.Playwright;

namespace NoonScraper.Crawler;

public sealed class StealthBrowser : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;

    public IPage Page { get; private set; }

    private StealthBrowser(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
    }

    // Headful mode is required, not just preferred - the target site hard-blocks
    // headless Chrome at the network layer even with these mitigations in place.
    // Locally that needs a real display (WSL); in CI it runs under xvfb-run.
    public static async Task<StealthBrowser> LaunchAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
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

        // Masks the cheap, static automation signals (navigator.webdriver, missing
        // chrome runtime, plugin/language fingerprints) that bot-detection scripts
        // check for before any real interaction happens. Does not defeat
        // behavioral analysis.
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

        return new StealthBrowser(playwright, browser, context, page);
    }

    // After a failed navigation the page can be left mid-load, on an error
    // document, or with a hung renderer. A fresh page in the same context (so the
    // stealth script and cookies carry over) keeps that from spreading to the
    // next product.
    public async Task ResetPageAsync()
    {
        try
        {
            await Page.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // Already gone - that's the situation this method exists for.
        }

        Page = await _context.NewPageAsync();
    }

    // Closes the browser out from under whatever is in flight, so a cancelled job
    // stops now instead of finishing a 20-second wait. In-flight Playwright calls
    // then fail, and the caller reports the cancellation.
    public async Task AbortAsync()
    {
        try
        {
            await _browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Each step may already have been torn down by AbortAsync.
        try
        {
            await _context.CloseAsync();
            await _browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }

        _playwright.Dispose();
    }
}
