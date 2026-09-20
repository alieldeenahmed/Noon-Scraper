using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace NoonScraper.Data;

// The single definition of "a noon.com product URL" - what the API accepts from
// users, and how every URL is written down before it's stored or compared.
//
// Accepting a URL from the internet and later having a browser visit it is the
// riskiest thing this project does, so TryParse is deliberately strict and the
// crawler only ever visits the canonical form built here (never the input).
public sealed partial record NoonUrl(string Canonical, string Market, string Sku)
{
    public const int MaxLength = 2048;

    private const string CanonicalHost = "www.noon.com";

    [GeneratedRegex("^[A-Za-z0-9]{2,64}$")]
    private static partial Regex SkuPattern();

    // "egypt-en", "uae-ar", ... - a lowercase market and a language.
    [GeneratedRegex("^[a-z]{2,}-(en|ar)$")]
    private static partial Regex LocalePattern();

    // What a path segment may contain: letters (any script - Arabic slugs are common), digits, and the few punctuation
    // marks that appear in real product slugs, plus percent-escapes (which is how a
    // non-Latin slug arrives). Anything else - commas, colons, quotes, an embedded
    // second URL - means this isn't a link copied from a product page.
    [GeneratedRegex("^[\\p{L}\\p{M}\\p{N}._~%+\\-]{1,512}$")]
    private static partial Regex SegmentPattern();

    private const int MaxSegments = 5;

    // Strict: user input. Only https, only noon.com / www.noon.com, no userinfo,
    // no non-default port, and a path shaped like a product page
    // (/<market>-<lang>/<slug>/<sku>/p/).
    public static bool TryParse(
        string? input,
        [NotNullWhen(true)] out NoonUrl? url,
        [NotNullWhen(false)] out string? error)
    {
        error = Validate(input?.Trim(), out url);
        return error is null;
    }

    private static string? Validate(string? input, out NoonUrl? parsed)
    {
        parsed = null;

        if (string.IsNullOrEmpty(input))
        {
            return "A product URL is required.";
        }

        if (input.Length > MaxLength)
        {
            return $"That URL is too long (max {MaxLength} characters).";
        }

        // Invisible characters (zero-width spaces and the like) are format
        // characters, not whitespace, and are a classic way to make two different
        // strings look identical.
        if (input.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) ||
                           char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
        {
            return "That URL contains whitespace or control characters.";
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return "That isn't a valid absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Only https:// links are accepted.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "URLs with embedded credentials are not accepted.";
        }

        if (!uri.IsDefaultPort)
        {
            return "URLs with a custom port are not accepted.";
        }

        // IdnHost is the ASCII (punycode) form, so a lookalike using non-Latin
        // characters can never compare equal to the real host.
        var host = uri.IdnHost;
        if (!host.Equals("noon.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals(CanonicalHost, StringComparison.OrdinalIgnoreCase))
        {
            return "Only noon.com product links are accepted.";
        }

        // Judged on the path as the user wrote it: Uri quietly percent-encodes
        // characters like < > " and |, which would let them through a whitelist
        // applied to the normalized path.
        var segments = RawPath(input).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments.Length > MaxSegments || segments[^1] != "p")
        {
            return "That doesn't look like a noon.com product page (expected .../<sku>/p/).";
        }

        var sku = segments[^2];
        if (!SkuPattern().IsMatch(sku))
        {
            return "That product link has an unrecognizable product code.";
        }

        var locale = segments[0];
        if (!LocalePattern().IsMatch(locale))
        {
            return "That product link has an unrecognizable market (expected e.g. egypt-en).";
        }

        if (!segments.All(s => SegmentPattern().IsMatch(s)))
        {
            return "That doesn't look like a noon.com product page (expected .../<sku>/p/).";
        }

        parsed = new NoonUrl(BuildCanonical(uri), locale.Split('-')[0], sku);
        return null;
    }

    private static string RawPath(string input)
    {
        var afterScheme = input.IndexOf("//", StringComparison.Ordinal) + 2;
        var pathStart = input.IndexOf('/', afterScheme);
        if (pathStart < 0)
        {
            return "";
        }

        var end = input.IndexOfAny(['?', '#'], pathStart);
        return end < 0 ? input[pathStart..] : input[pathStart..end];
    }

    // Lenient: for links the scrapers read off Noon's own pages. No shape
    // checks, but the same canonical form, so a scraped URL and a submitted one
    // for the same page are byte-for-byte equal.
    public static string Canonicalize(string url) => BuildCanonical(new Uri(url));

    // Noon appends a per-session tracking query string (?o=...&pcl=...) to every
    // product link, so comparing raw URLs treats the same product as a different
    // one on every crawl. Dropping query and fragment, forcing the www host, and
    // ending the path in "/" gives a stable identity.
    private static string BuildCanonical(Uri uri)
    {
        var host = uri.IdnHost.Equals("noon.com", StringComparison.OrdinalIgnoreCase) ? CanonicalHost : uri.Host;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return $"{uri.Scheme}://{host}{path}";
    }

    // The product code out of a relative or absolute product link: the path
    // segment right before the trailing "p". A product path is at least
    // /<locale>/<slug>/<sku>/p, so a "p" any earlier than the fourth position
    // (e.g. /egypt-en/p/) isn't one - and its preceding segment isn't a code.
    public static string? ExtractSku(string hrefOrUrl)
    {
        var path = hrefOrUrl.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pIndex = Array.LastIndexOf(segments, "p");
        return pIndex >= 2 ? segments[pIndex - 1] : null;
    }
}
