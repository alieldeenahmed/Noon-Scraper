using NoonScraper.Data.Models;

namespace NoonScraper.Api.Dtos;

public class ProductListItemDto
{
    public required int Id { get; set; }

    public required string Url { get; set; }

    public string? Name { get; set; }

    public Category? Category { get; set; }

    public string? MerchantName { get; set; }

    public decimal? Rating { get; set; }

    public required bool IsActive { get; set; }

    public decimal? LatestPrice { get; set; }

    public decimal? LatestDiscountPercent { get; set; }

    public bool? LatestStock { get; set; }

    public DateTimeOffset? LastCrawledAt { get; set; }
}
