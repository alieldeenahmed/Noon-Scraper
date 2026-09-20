namespace NoonScraper.Crawler;

// What the crawl jobs need from "a browser": read a category page, a product
// page, or a product's offers. The jobs (daily crawl, crawl-product, check-now)
// only ever see this interface, so their failure handling - retries, isolation,
// state transitions, exit codes - is testable without launching Chrome, by
// substituting a session that fails on demand.
public interface IScrapeSession : IAsyncDisposable
{
    Task<CategoryScrapeResult> ScrapeCategoryAsync(string url, CancellationToken ct);

    Task<ScrapedProduct?> ScrapeProductAsync(string url, CancellationToken ct);

    Task<IReadOnlyList<OfferResult>> ScrapeOffersAsync(string url, CancellationToken ct);

    // A fresh page, called after a failed scrape so one bad page can't poison the next.
    Task ResetAsync(CancellationToken ct);
}

public interface IScrapeSessionFactory
{
    Task<IScrapeSession> OpenAsync(CancellationToken ct);
}

public sealed class BrowserScrapeSessionFactory : IScrapeSessionFactory
{
    public async Task<IScrapeSession> OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new BrowserScrapeSession(await StealthBrowser.LaunchAsync());
    }
}

public sealed class BrowserScrapeSession(StealthBrowser browser) : IScrapeSession
{
    public Task<CategoryScrapeResult> ScrapeCategoryAsync(string url, CancellationToken ct) =>
        RunAsync(() => CategoryScraper.ScrapeAsync(browser.Page, url), ct);

    public Task<ScrapedProduct?> ScrapeProductAsync(string url, CancellationToken ct) =>
        RunAsync(() => ProductPageScraper.ScrapeAsync(browser.Page, url), ct);

    public async Task<IReadOnlyList<OfferResult>> ScrapeOffersAsync(string url, CancellationToken ct) =>
        await RunAsync(() => OfferScraper.ScrapeOffersAsync(browser.Page, url), ct);

    public Task ResetAsync(CancellationToken ct) => browser.ResetPageAsync();

    public ValueTask DisposeAsync() => browser.DisposeAsync();

    // Playwright calls can't take a CancellationToken, so cancellation is
    // enforced by closing the browser: the pending call fails immediately and the
    // caller sees the token is cancelled.
    private async Task<T> RunAsync<T>(Func<Task<T>> scrape, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(() => _ = browser.AbortAsync());

        try
        {
            return await scrape();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
