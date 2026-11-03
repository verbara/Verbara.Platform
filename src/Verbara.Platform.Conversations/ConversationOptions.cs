namespace Verbara.Platform.Conversations;

/// <summary>
/// Configuration options for the conversations subsystem, bound from the <c>Conversations</c> section by the Api
/// host (explicit <c>TryParse</c>, AOT-safe; conversations-close-after-inactivity design D1).
/// </summary>
public sealed class ConversationOptions
{
    /// <summary>
    /// Default wrap-up time in seconds after a conversation ends.
    /// </summary>
    public int DefaultWrapUpSeconds { get; set; } = 30;

    /// <summary>
    /// <c>Conversations:InactivityCloseEnabled</c>: whether the timeout worker closes open digital conversations that
    /// had no customer-facing message for <see cref="InactivityCloseAfterHours"/>. On by default; the host-wide off
    /// switch (a restart applies it).
    /// </summary>
    public bool InactivityCloseEnabled { get; set; } = true;

    /// <summary>
    /// <c>Conversations:InactivityCloseAfterHours</c>: the host's inactivity window, the default for every tenant
    /// (a tenant may override it, design D11). Minimum 1. Below 24 h is allowed with one startup warning: WhatsApp
    /// conversations then close inside Meta's 24 h customer-service window.
    /// </summary>
    public int InactivityCloseAfterHours { get; set; } = 72;

    /// <summary>
    /// <c>Conversations:InactivityCloseSweepIntervalSeconds</c>: the inactivity pass runs at most once per this
    /// interval per replica. 5 to 3600.
    /// </summary>
    public int InactivityCloseSweepIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// <c>Conversations:InactivityCloseBatchSize</c>: the most conversations one tenant closes in one pass, which
    /// paces the first-deploy wave. 1 to 1000.
    /// </summary>
    public int InactivityCloseBatchSize { get; set; } = 100;
}
