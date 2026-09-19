namespace NoonScraper.Api.Dtos;

public class PagedResultDto<T>
{
    public required List<T> Items { get; set; }

    public required int Total { get; set; }

    public required int Page { get; set; }

    public required int PageSize { get; set; }

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}
