namespace NoonScraper.Api.Models;

public class PriceSnapshot
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public required decimal Price { get; set; }

    public required bool Stock { get; set; }

    public decimal? DiscountPercent { get; set; }

    public DateTimeOffset CrawledAt { get; set; } = DateTimeOffset.UtcNow;
}
