namespace Asterisk.Platform.Billing;

/// <summary>
/// Classifies the type of billable consumption event.
/// </summary>
public enum UsageType
{
    VoiceInbound,
    VoiceOutbound,
    SmsInbound,
    SmsOutbound,
    WhatsAppInbound,
    WhatsAppOutbound,
    EmailInbound,
    EmailOutbound,
    WebChatSession,
    TelegramInbound,
    TelegramOutbound,
    RecordingStorage,
    MediaStorage,
    DialerAttempt,
    DialerConnected,
    AgentLoginHour,
    AiAnalysis,
}
