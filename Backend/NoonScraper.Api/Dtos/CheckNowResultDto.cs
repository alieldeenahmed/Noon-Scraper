namespace NoonScraper.Api.Dtos;

public class CheckNowResultDto
{
    public required int RequestId { get; set; }

    public required string Status { get; set; }

    public decimal? LowestPrice { get; set; }

    public string? LowestPriceMerchant { get; set; }

    public List<OfferDto>? Offers { get; set; }

    public string? ErrorMessage { get; set; }
}
