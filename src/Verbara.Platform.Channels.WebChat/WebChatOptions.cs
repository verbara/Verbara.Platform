namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatOptions
{
    /// <summary>The host setting behind <see cref="VisitorLevel1Enabled"/>.</summary>
    public const string VisitorLevel1EnabledKey = "WebChat:VisitorIdentity:Level1Enabled";

    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxMessageLength { get; set; } = 4000;
    public bool EnableTypingIndicators { get; set; } = true;
    public bool EnableReadReceipts { get; set; } = true;

    /// <summary>
    /// Whether a session may find its contact by the widget's <c>visitorId</c>. Off by default, so a host links no
    /// returning visitor until the console that marks an unverified contact is deployed. A verified token does not
    /// depend on it.
    /// </summary>
    public bool VisitorLevel1Enabled { get; set; }
}
