using NoonScraper.Data;

namespace NoonScraper.Tests;

// The rules for accepting a URL from the internet and later sending a browser to
// it. The history here matters: the first version checked EndsWith("noon.com"),
// which accepted evilnoon.com.
public class NoonUrlTests
{
    private const string Good = "https://www.noon.com/egypt-en/some-product/N123/p/";

    private static NoonUrl Parse(string input)
    {
        Assert.True(NoonUrl.TryParse(input, out var url, out var error), $"'{input}' was rejected: {error}");
        return url;
    }

    private static string Reject(string? input)
    {
        Assert.False(NoonUrl.TryParse(input, out var url, out var error), $"'{input}' was accepted as {url?.Canonical}");
        Assert.NotNull(error);
        return error;
    }

    public class Accepted
    {
        [Fact]
        public void A_plain_product_url_is_unchanged()
        {
            var url = Parse(Good);

            Assert.Equal(Good, url.Canonical);
            Assert.Equal("egypt", url.Market);
            Assert.Equal("N123", url.Sku);
        }

        [Theory]
        [InlineData("https://www.noon.com/egypt-en/some-product/N123/p/?o=abc123&pcl=xyz")]
        [InlineData("https://www.noon.com/egypt-en/some-product/N123/p/#reviews")]
        [InlineData("https://www.noon.com/egypt-en/some-product/N123/p/?o=1#top")]
        [InlineData("https://noon.com/egypt-en/some-product/N123/p/")]
        [InlineData("https://NOON.COM/egypt-en/some-product/N123/p/")]
        [InlineData("https://WWW.Noon.Com/egypt-en/some-product/N123/p/")]
        [InlineData("https://www.noon.com/egypt-en/some-product/N123/p")]
        [InlineData("https://www.noon.com:443/egypt-en/some-product/N123/p/")]
        [InlineData("  https://www.noon.com/egypt-en/some-product/N123/p/  ")]
        [InlineData("\thttps://www.noon.com/egypt-en/some-product/N123/p/\n")]
        public void Different_spellings_of_the_same_page_share_one_canonical_form(string input)
        {
            Assert.Equal(Good, Parse(input).Canonical);
        }

        [Fact]
        public void Market_and_sku_come_from_the_path()
        {
            var url = Parse("https://www.noon.com/uae-ar/ثلاجة/Z687035FCE6B18D5DB6C2Z/p/");

            Assert.Equal("uae", url.Market);
            Assert.Equal("Z687035FCE6B18D5DB6C2Z", url.Sku);
        }

        [Fact]
        public void A_non_latin_slug_is_kept_percent_encoded()
        {
            var url = Parse("https://www.noon.com/egypt-ar/كوب-ستانلس/N1/p/");

            Assert.StartsWith("https://www.noon.com/egypt-ar/", url.Canonical);
            Assert.DoesNotContain("ك", url.Canonical);
            Assert.EndsWith("/N1/p/", url.Canonical);
        }

        [Fact]
        public void The_english_and_arabic_pages_of_one_product_share_market_and_sku()
        {
            var english = Parse("https://www.noon.com/egypt-en/thing/N123/p/");
            var arabic = Parse("https://www.noon.com/egypt-ar/thing/N123/p/");

            Assert.NotEqual(english.Canonical, arabic.Canonical);
            Assert.Equal((english.Market, english.Sku), (arabic.Market, arabic.Sku));
        }

        // Whatever gets in, what comes out is safe to hand to a browser: always
        // https, always our host, never carrying a query, fragment or credentials.
        [Theory]
        [MemberData(nameof(AcceptedInputs))]
        public void Canonical_output_always_has_the_same_safe_shape(string input)
        {
            var canonical = Parse(input).Canonical;

            Assert.StartsWith("https://www.noon.com/", canonical);
            Assert.DoesNotContain("?", canonical);
            Assert.DoesNotContain("#", canonical);
            Assert.DoesNotContain("@", canonical);
            Assert.EndsWith("/p/", canonical);
        }

        public static IEnumerable<object[]> AcceptedInputs() =>
        [
            [Good],
            ["https://noon.com/egypt-en/a/N1/p/?x=1#y"],
            ["https://www.noon.com/egypt-en/a/b/N1/p"],
            ["  https://www.noon.com/uae-en/a/N1/p/  "]
        ];
    }

    public class Rejected
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Nothing_is_rejected(string? input) => Assert.Contains("required", Reject(input));

        [Theory]
        [InlineData("not a url")]
        [InlineData("noon.com/egypt-en/a/N1/p/")]
        [InlineData("//www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("/egypt-en/a/N1/p/")]
        [InlineData("https://")]
        public void A_value_that_is_not_an_absolute_url_is_rejected(string input) => Reject(input);

        [Theory]
        [InlineData("http://www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("ftp://www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("file:///etc/passwd")]
        [InlineData("javascript:alert(1)")]
        [InlineData("data:text/html,<script>alert(1)</script>")]
        [InlineData("ws://www.noon.com/egypt-en/a/N1/p/")]
        public void Only_https_is_accepted(string input) => Assert.Contains("https", Reject(input));

        [Theory]
        // the original bug: a bare EndsWith("noon.com") accepted this
        [InlineData("https://evilnoon.com/egypt-en/a/N1/p/")]
        [InlineData("https://notnoon.com/egypt-en/a/N1/p/")]
        [InlineData("https://noon.com.evil.com/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com.evil.com/egypt-en/a/N1/p/")]
        [InlineData("https://evil.com/noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://evil.com/?u=https://www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.co/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com.au/egypt-en/a/N1/p/")]
        [InlineData("https://noon.com./egypt-en/a/N1/p/")]
        [InlineData("https://sub.noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://cdn.noon.com/egypt-en/a/N1/p/")]
        // homoglyphs: Cyrillic 'о', and the punycode form of such a host
        [InlineData("https://noоn.com/egypt-en/a/N1/p/")]
        [InlineData("https://xn--nn-jmc.com/egypt-en/a/N1/p/")]
        // addresses, not names
        [InlineData("https://127.0.0.1/egypt-en/a/N1/p/")]
        [InlineData("https://localhost/egypt-en/a/N1/p/")]
        [InlineData("https://[::1]/egypt-en/a/N1/p/")]
        [InlineData("https://169.254.169.254/latest/meta-data/egypt-en/N1/p/")]
        public void Only_noon_dot_com_is_accepted(string input) => Assert.Contains("noon.com", Reject(input));

        [Theory]
        [InlineData("https://noon.com@evil.com/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com@evil.com/egypt-en/a/N1/p/")]
        [InlineData("https://user:pass@www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com:secret@evil.com/egypt-en/a/N1/p/")]
        public void Credentials_in_the_url_are_rejected(string input)
        {
            Assert.Contains("credentials", Reject(input));
        }

        [Theory]
        [InlineData("https://www.noon.com:8443/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com:80/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com:22/egypt-en/a/N1/p/")]
        public void A_custom_port_is_rejected(string input) => Assert.Contains("port", Reject(input));

        [Theory]
        [InlineData("https://www.noon.com/")]
        [InlineData("https://www.noon.com")]
        [InlineData("https://www.noon.com/egypt-en/")]
        [InlineData("https://www.noon.com/egypt-en/mobiles/")]
        [InlineData("https://www.noon.com/egypt-en/search/?q=phone")]
        [InlineData("https://www.noon.com/egypt-en/x/N1/")]
        [InlineData("https://www.noon.com/egypt-en/x/N1/p/extra/")]
        [InlineData("https://www.noon.com/p/")]
        [InlineData("https://www.noon.com/egypt-en/p/")]
        [InlineData("https://www.noon.com/egypt-en/x/N1/P/")]
        public void Only_product_pages_are_accepted(string input) => Assert.Contains("product page", Reject(input));

        [Theory]
        [InlineData("https://www.noon.com/egypt-en/x/N/p/")]
        [InlineData("https://www.noon.com/egypt-en/x/N-1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x/N%2F1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x/N1!/p/")]
        [InlineData("https://www.noon.com/egypt-en/x/12345678901234567890123456789012345678901234567890123456789012345/p/")]
        public void An_unrecognizable_product_code_is_rejected(string input) => Assert.Contains("product code", Reject(input));

        [Theory]
        [InlineData("https://www.noon.com/Egypt-EN/x/N1/p/")]
        [InlineData("https://www.noon.com/egypt_en/x/N1/p/")]
        [InlineData("https://www.noon.com/eg1/x/N1/p/")]
        [InlineData("https://www.noon.com/../x/N1/p/")]
        [InlineData("https://www.noon.com/@evil.com/x/N1/p/")]
        public void An_unrecognizable_market_is_rejected(string input)
        {
            Reject(input);
        }

        // Backslashes are treated as slashes by URL parsers: this looks like it
        // points at evil.com but is really www.noon.com, with a hostile-looking
        // first path segment - which fails the market check.
        [Fact]
        public void A_backslash_authority_trick_is_rejected()
        {
            Reject("https://www.noon.com\\@evil.com/egypt-en/x/N1/p/");
        }

        [Theory]
        [InlineData("https://www.noon.com/egypt-en/x y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x\ty/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x\ny/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x​y/N1/p/")]
        public void Whitespace_and_control_characters_are_rejected(string input) => Reject(input);

        [Fact]
        public void An_overlong_url_is_rejected()
        {
            var input = "https://www.noon.com/egypt-en/" + new string('a', NoonUrl.MaxLength) + "/N1/p/";

            Assert.Contains("too long", Reject(input));
        }

        [Theory]
        [InlineData("https://www.noon.com/egypt-en/x,y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x;y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x:y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x\"y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x'y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x|y/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/x<y>/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/a/b/c/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/a/b/c/d/e/f/N1/p/")]
        public void A_path_that_is_not_the_shape_of_a_product_link_is_rejected(string input)
        {
            Assert.Contains("product page", Reject(input));
        }

        [Fact]
        public void Two_urls_in_one_string_are_rejected()
        {
            Reject($"{Good} {Good}");
            Reject($"{Good},{Good}");
        }
    }

    public class Scraped
    {
        [Theory]
        [InlineData("https://www.noon.com/egypt-en/a/N1/p/?o=abc", "https://www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://noon.com/egypt-en/a/N1/p/", "https://www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/a/N1/p", "https://www.noon.com/egypt-en/a/N1/p/")]
        [InlineData("https://www.noon.com/egypt-en/a/N1/p/#x", "https://www.noon.com/egypt-en/a/N1/p/")]
        public void Canonicalize_matches_what_a_submission_would_produce(string scraped, string expected)
        {
            Assert.Equal(expected, NoonUrl.Canonicalize(scraped));
            Assert.Equal(expected, UrlNormalizer.Normalize(scraped));
        }

        [Fact]
        public void A_scraped_and_a_submitted_url_for_the_same_page_are_byte_for_byte_equal()
        {
            var submitted = Parse("https://noon.com/egypt-en/a/N1/p/?o=abc").Canonical;
            var scraped = NoonUrl.Canonicalize("https://www.noon.com/egypt-en/a/N1/p/?o=zzz&pcl=1");

            Assert.Equal(submitted, scraped);
        }

        [Fact]
        public void Canonicalize_still_throws_on_something_that_is_not_a_url()
        {
            Assert.Throws<UriFormatException>(() => NoonUrl.Canonicalize("not a url"));
        }

        [Theory]
        [InlineData("/egypt-en/some-slug/N70202442V/p/?o=ddc1b0bb", "N70202442V")]
        [InlineData("/egypt-en/x/N1/p/", "N1")]
        [InlineData("https://www.noon.com/egypt-en/x/N1/p/#top", "N1")]
        [InlineData("/egypt-en/p/", null)]
        [InlineData("/p/", null)]
        [InlineData("/egypt-en/mobiles/", null)]
        [InlineData("", null)]
        [InlineData("p", null)]
        public void The_sku_is_the_segment_before_the_trailing_p(string href, string? expected)
        {
            Assert.Equal(expected, NoonUrl.ExtractSku(href));
        }
    }
}
