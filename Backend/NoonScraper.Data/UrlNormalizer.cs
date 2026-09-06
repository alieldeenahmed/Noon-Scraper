namespace NoonScraper.Data;

public static class UrlNormalizer
{
    // Noon appends a per-crawl-session tracking query string (?o=...&pcl=...) to
    // every product link, so comparing/storing raw URLs treats the same product
    // as a different one on every crawl. Stripping the query string down to just
    // scheme+host+path gives a stable identity for the same product over time.
    public static string Normalize(string url)
    {
        var uri = new Uri(url);
        return uri.GetLeftPart(UriPartial.Path);
    }
}
