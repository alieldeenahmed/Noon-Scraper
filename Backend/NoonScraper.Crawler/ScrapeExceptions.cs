namespace NoonScraper.Crawler;

// Failures the crawler distinguishes on purpose. The difference matters twice:
// retrying is only worth doing for problems that can go away, and the text that
// ends up on a request row (and so in the public API) has to be safe to show.

// The page loaded but its data isn't the shape we expect - a markup or
// structured-data change. Retrying won't help.
public class ScrapeParseException(string message) : Exception(message);

// The page loaded but had no product data at all - typically an anti-bot
// interstitial or an error page served with a 200. Might be transient.
public class ScrapeNoDataException(string message) : Exception(message);

// noon.com answered with an error status.
public class ScrapeNavigationException(int status, string url)
    : Exception($"noon.com returned HTTP {status}")
{
    public int Status { get; } = status;

    // For the log; deliberately not in the message, which can reach a public API response.
    public string Url { get; } = url;

    // Blocked, rate limited, or a server error: may clear up. A 404/410 means the
    // product page is gone, which retrying won't fix.
    public bool IsTransient => Status is 403 or 408 or 429 or >= 500;
}
