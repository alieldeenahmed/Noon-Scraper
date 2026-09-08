namespace NoonScraper.Api.Dtos;

public class DiscountFlagDto
{
    public required decimal PriorHighPrice { get; set; }

    public required DateTimeOffset PriorHighDetectedAt { get; set; }

    public required decimal DiscountedPrice { get; set; }

    public required decimal DiscountPercent { get; set; }

    public required DateTimeOffset DetectedAt { get; set; }
}
