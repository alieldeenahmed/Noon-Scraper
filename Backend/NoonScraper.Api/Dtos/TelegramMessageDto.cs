namespace NoonScraper.Api.Dtos;

public class TelegramMessageDto
{
    // Nullable: this is a public endpoint, so a payload without a chat is just
    // another malformed request to ignore, not a null dereference.
    public TelegramChatDto? Chat { get; set; }

    public string? Text { get; set; }
}
