namespace NoonScraper.Crawler;

public class ScrapedProduct
{
    public required string NoonProductId { get; set; }

    public required string Url { get; set; }

    public required string Name { get; set; }

    public required decimal Price { get; set; }

    public decimal? DiscountPercent { get; set; }

    public decimal? Rating { get; set; }

    // Noon's category listing tiles show no out-of-stock indicator in what we've
    // observed, so this is always true for now - refine once an out-of-stock
    // example is found.
    public bool Stock { get; set; } = true;
}
