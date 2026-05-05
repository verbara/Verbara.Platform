namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatOptions
{
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxMessageLength { get; set; } = 4000;
    public bool EnableTypingIndicators { get; set; } = true;
    public bool EnableReadReceipts { get; set; } = true;
}
