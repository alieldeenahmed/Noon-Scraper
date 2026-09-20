using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoonScraper.Crawler;

// Turns the text Noon shows for prices, discounts and ratings into numbers.
// One copy - CategoryScraper and OfferScraper each used to carry their own.
public static partial class PriceText
{
    // First numeric token: a digit, then digits and thousands commas, then an
    // optional decimal part. Surrounding text ("EGP", "% OFF", Arabic words) is
    // ignored, which is what makes badges like "خصم 6%" and "16% OFF" both work.
    [GeneratedRegex(@"[0-9][0-9,]*(\.[0-9]+)?")]
    private static partial Regex NumberPattern();

    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = NumberPattern().Match(NormalizeDigits(text));
        return match.Success && decimal.TryParse(
            match.Value.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static decimal? TryParseOrNull(string? text) => TryParse(text, out var value) ? value : null;

    public static decimal Parse(string? text) =>
        TryParse(text, out var value)
            ? value
            : throw new ScrapeParseException($"No numeric value found in '{Truncate(text)}'");

    // Arabic-Indic (٠-٩) and Extended Arabic-Indic (۰-۹) digits, and the Arabic
    // decimal and thousands separators, mapped to their ASCII equivalents so the
    // page's locale can't turn a price into a parse failure.
    private static string NormalizeDigits(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                '٫' => '.',
                '٬' => ',',
                _ => c
            });
        }

        return builder.ToString();
    }

    private static string Truncate(string? text) =>
        text is null ? "" : text.Length <= 40 ? text : text[..40] + "…";
}
