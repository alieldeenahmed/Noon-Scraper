namespace NoonScraper.Api.Dtos;

public class OfferDto
{
    public required string MerchantName { get; set; }

    public required decimal Price { get; set; }

    public decimal? Rating { get; set; }
}
