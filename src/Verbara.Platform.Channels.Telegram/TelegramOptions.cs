namespace Verbara.Platform.Channels.Telegram;

/// <summary>Options for the Telegram Bot API connector.</summary>
public sealed class TelegramOptions
{
    /// <summary>Telegram Bot API token (from BotFather).</summary>
    public required string BotToken { get; set; }

    /// <summary>Optional secret token used to validate incoming webhook requests (X-Telegram-Bot-Api-Secret-Token header).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Telegram Bot API base URL. Default: https://api.telegram.org.</summary>
    public string BaseUrl { get; set; } = "https://api.telegram.org";
}
