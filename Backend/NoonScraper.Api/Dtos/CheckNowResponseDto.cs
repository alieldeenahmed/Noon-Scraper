namespace NoonScraper.Api.Dtos;

public class CheckNowResponseDto
{
    public required int ProductId { get; set; }

    public required decimal LowestPrice { get; set; }

    public required string LowestPriceMerchant { get; set; }

    public required List<OfferDto> Offers { get; set; }
}
