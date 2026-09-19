using NoonScraper.Data;

namespace NoonScraper.Tests;

public class UrlNormalizerTests
{
    private const string Base = "https://www.noon.com/egypt-en/some-product/N123/p/";

    [Fact]
    public void Strips_the_tracking_query_string()
    {
        var result = UrlNormalizer.Normalize($"{Base}?o=abc123&pcl=xyz");

        Assert.Equal(Base, result);
    }

    [Fact]
    public void Same_product_with_different_tracking_tokens_normalizes_identically()
    {
        var first = UrlNormalizer.Normalize($"{Base}?o=aaa&pcl=111");
        var second = UrlNormalizer.Normalize($"{Base}?o=bbb&pcl=222");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Leaves_a_url_without_a_query_string_unchanged()
    {
        Assert.Equal(Base, UrlNormalizer.Normalize(Base));
    }

    [Fact]
    public void Strips_a_fragment_too()
    {
        Assert.Equal(Base, UrlNormalizer.Normalize($"{Base}#reviews"));
    }

    [Fact]
    public void Different_products_stay_different()
    {
        var a = UrlNormalizer.Normalize("https://www.noon.com/egypt-en/product-a/N1/p/?o=x");
        var b = UrlNormalizer.Normalize("https://www.noon.com/egypt-en/product-b/N2/p/?o=x");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Preserves_scheme_host_and_path()
    {
        var result = UrlNormalizer.Normalize("https://www.noon.com/egypt-ar/some-product/N9/p/?o=1");

        Assert.Equal("https://www.noon.com/egypt-ar/some-product/N9/p/", result);
    }

    [Fact]
    public void Throws_on_a_value_that_is_not_an_absolute_url()
    {
        Assert.Throws<UriFormatException>(() => UrlNormalizer.Normalize("not a url"));
    }
}
