namespace NoonScraper.Api.Dtos;

public class ProductStatsDto
{
    public required int Total { get; set; }

    public required int InStock { get; set; }

    public required int OnDiscount { get; set; }
}
