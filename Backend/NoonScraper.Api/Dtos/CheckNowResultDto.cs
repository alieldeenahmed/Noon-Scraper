namespace NoonScraper.Api.Dtos;

public class CheckNowResultDto
{
    public required int RequestId { get; set; }

    public required string Status { get; set; }

    public decimal? LowestPrice { get; set; }

    public string? LowestPriceMerchant { get; set; }

    public List<OfferDto>? Offers { get; set; }

    public string? ErrorMessage { get; set; }

    public string? FailureStage { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? RunUrl { get; set; }
}
