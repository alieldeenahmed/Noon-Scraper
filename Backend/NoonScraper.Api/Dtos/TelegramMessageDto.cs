namespace NoonScraper.Api.Dtos;

public class TelegramMessageDto
{
    public TelegramChatDto Chat { get; set; } = null!;

    public string? Text { get; set; }
}
