namespace Verbara.Platform.Core;

public enum PlanFeature
{
    Dialer,
    BotBasic,
    BotAdvanced,
    AgentAssist,
    CallAnalytics,
    AnalyticsExport,
    Flows,
    Webhooks,
    OidcSso,
    ScheduledReports,
    KnowledgeBase,
    Recordings,
    RecordingTranscription,
    IpAllowlist,
    /// <summary>Entitlement to use Verbara's platform-managed Typification LLM (metered in AI Credits).</summary>
    PlatformLlm,
}
