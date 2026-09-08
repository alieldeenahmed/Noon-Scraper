namespace NoonScraper.Api.Dtos;

// Only the subset of Telegram's Update object this app actually reads -
// see https://core.telegram.org/bots/api#update for the full shape.
public class TelegramUpdateDto
{
    public TelegramMessageDto? Message { get; set; }
}
