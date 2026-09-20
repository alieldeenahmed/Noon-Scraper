namespace NoonScraper.Api.Dtos;

public class TelegramChatDto
{
    public long Id { get; set; }

    // "private", "group", "supergroup" or "channel". Subscriptions are only for
    // private chats: a group has no single owner to /stop them.
    public string? Type { get; set; }
}
