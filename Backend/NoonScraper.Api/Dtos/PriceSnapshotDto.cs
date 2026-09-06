namespace NoonScraper.Api.Dtos;

public class PriceSnapshotDto
{
    public required decimal Price { get; set; }

    public required bool Stock { get; set; }

    public decimal? DiscountPercent { get; set; }

    public required DateTimeOffset CrawledAt { get; set; }
}
