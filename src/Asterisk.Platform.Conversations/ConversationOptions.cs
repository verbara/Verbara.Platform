namespace Asterisk.Platform.Conversations;

/// <summary>
/// Configuration options for the conversations subsystem.
/// </summary>
public sealed class ConversationOptions
{
    /// <summary>
    /// Default wrap-up time in seconds after a conversation ends.
    /// </summary>
    public int DefaultWrapUpSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to automatically close resolved conversations after a period.
    /// </summary>
    public bool AutoCloseResolvedAfterHours { get; set; } = true;

    /// <summary>
    /// Hours after which resolved conversations are automatically closed.
    /// </summary>
    public int AutoCloseResolvedHours { get; set; } = 72;
}
