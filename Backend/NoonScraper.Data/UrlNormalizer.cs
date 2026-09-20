namespace NoonScraper.Data;

// Kept as the scrapers' entry point; the rules live in NoonUrl.
public static class UrlNormalizer
{
    public static string Normalize(string url) => NoonUrl.Canonicalize(url);
}
