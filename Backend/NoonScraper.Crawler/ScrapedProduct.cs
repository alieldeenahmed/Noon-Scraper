namespace NoonScraper.Crawler;

public class ScrapedProduct
{
    public required string NoonProductId { get; set; }

    public required string Url { get; set; }

    public required string Name { get; set; }

    public required decimal Price { get; set; }

    public decimal? DiscountPercent { get; set; }

    public decimal? Rating { get; set; }

    public string? MerchantName { get; set; }

    // Category listing tiles show no out-of-stock indicator, so CategoryScraper
    // always leaves this true. ProductPageScraper sets it from the product detail
    // page's actual availability data instead.
    public bool Stock { get; set; } = true;
}
