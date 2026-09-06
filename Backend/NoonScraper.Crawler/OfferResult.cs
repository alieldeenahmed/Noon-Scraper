namespace NoonScraper.Crawler;

public class OfferResult
{
    public required string MerchantName { get; set; }

    public required decimal Price { get; set; }

    public decimal? Rating { get; set; }
}
