using Microsoft.Playwright;

namespace NoonScraper.Api.Services;

// Launches a Chrome instance with the same stealth mitigations the Crawler uses
// (masks static automation signals Akamai's bot detection checks for). Headful
// mode is required - headless Chrome is hard-blocked at the network layer even
// with these mitigations. Locally this works because WSL provides a display;
// deploying this to a real server will need Xvfb wrapping the process.
public sealed class StealthBrowserSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;

    public IPage Page { get; }

    private StealthBrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
    }

    public static async Task<StealthBrowserSession> LaunchAsync()
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

        return new StealthBrowserSession(playwright, browser, context, page);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.CloseAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
    }
}
