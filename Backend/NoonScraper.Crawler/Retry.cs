using Microsoft.Playwright;

namespace NoonScraper.Crawler;

public sealed class CrawlOptions
{
    public const string SectionName = "Crawl";

    // Attempts per scrape, including the first. Retries are for problems that can
    // clear up on their own (a timeout, a 429, a hung page); they are not a way to
    // hammer a site that is actively blocking us.
    public int MaxAttempts { get; set; } = 2;

    // Delay before retry N is RetryDelay * N.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    // The scheduled crawl stops starting new work after this long, so a large
    // backlog ends with a clean summary instead of the workflow's hard timeout
    // killing it mid-product. Unreached products are crawled first next time.
    public TimeSpan Budget { get; set; } = TimeSpan.FromMinutes(15);

    // A scheduled run counts as failed (non-zero exit, red workflow) when at least
    // this share of the products it attempted failed. A stray delisted product
    // shouldn't turn every run red; a broken selector should.
    public double MaxFailureRate { get; set; } = 0.2;

    // How long a request may sit in Pending/Running before it is considered dead.
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);
}

public static class ScrapeErrors
{
    // Whether trying again could plausibly succeed. Parse errors and 404s can't:
    // the page is what it is. Timeouts, browser hiccups, blocks, and empty pages
    // (often an interstitial) can.
    public static bool IsTransient(Exception ex) => ex switch
    {
        ScrapeParseException => false,
        ScrapeNavigationException nav => nav.IsTransient,
        ScrapeNoDataException => true,
        TimeoutException => true,
        PlaywrightException => true,
        _ => false
    };

    // A short description that is safe to store on a request row and show through
    // the public API. Exception messages from other libraries can contain
    // connection details or file paths, so unrecognized exceptions are reduced to
    // their type; the full detail goes to the logs, which is where it belongs.
    public static string Describe(Exception ex) => ex switch
    {
        ScrapeParseException or ScrapeNoDataException or ScrapeNavigationException => ex.Message,
        OperationCanceledException => "Cancelled: the workflow run was stopped or timed out.",
        TimeoutException or PlaywrightException => "The page didn't load in time, or the browser failed.",
        _ => $"Unexpected {ex.GetType().Name}; see the workflow run logs."
    };
}

public static class Retry
{
    // Runs `action` up to MaxAttempts times, retrying only when `isTransient` says
    // the failure is worth it. `onRetry` runs between attempts (the crawl uses it
    // to open a fresh page). The last failure is thrown as-is - retrying never
    // hides what went wrong.
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> action,
        int maxAttempts,
        TimeSpan delay,
        TimeProvider clock,
        Func<Exception, int, Task>? onRetry,
        CancellationToken ct,
        Func<Exception, bool>? isTransient = null)
    {
        isTransient ??= ScrapeErrors.IsTransient;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && isTransient(ex))
            {
                if (onRetry is not null)
                {
                    await onRetry(ex, attempt);
                }

                await Task.Delay(delay * attempt, clock, ct);
            }
        }
    }
}
