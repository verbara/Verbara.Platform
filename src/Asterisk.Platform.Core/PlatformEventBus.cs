using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Asterisk.Platform.Core;

/// <summary>
/// Lightweight in-process event bus for broadcasting platform events to SSE subscribers.
/// </summary>
public sealed class PlatformEventBus : IDisposable
{
    private readonly Subject<PlatformEvent> _events = new();

    /// <summary>
    /// Observable stream of all platform events.
    /// </summary>
    public IObservable<PlatformEvent> Events => _events.AsObservable();

    /// <summary>
    /// Publishes an event to all subscribers.
    /// </summary>
    public void Publish(PlatformEvent evt) => _events.OnNext(evt);

    public void Dispose() => _events.Dispose();
}

/// <summary>
/// Base record for all platform events distributed via <see cref="PlatformEventBus"/>.
/// </summary>
public abstract record PlatformEvent(string TenantId, string Type, DateTimeOffset Timestamp);

/// <summary>Raised when a conversation is assigned to an agent.</summary>
public sealed record ConversationAssignedEvent(
    string TenantId,
    string ConversationId,
    string AgentId,
    string QueueName,
    string Channel,
    string ContactName)
    : PlatformEvent(TenantId, "conversation.assigned", DateTimeOffset.UtcNow);

/// <summary>Raised when a new message arrives in a conversation.</summary>
public sealed record ConversationMessageEvent(
    string TenantId,
    string ConversationId,
    string MessageId,
    string Sender,
    string Text)
    : PlatformEvent(TenantId, "conversation.message", DateTimeOffset.UtcNow);

/// <summary>Raised when a conversation changes state.</summary>
public sealed record ConversationStateChangedEvent(
    string TenantId,
    string ConversationId,
    string OldState,
    string NewState)
    : PlatformEvent(TenantId, "conversation.state_changed", DateTimeOffset.UtcNow);

/// <summary>Raised when an agent's presence state changes.</summary>
public sealed record AgentStateChangedEvent(
    string TenantId,
    string AgentId,
    string AgentName,
    string OldState,
    string NewState)
    : PlatformEvent(TenantId, "agent.state_changed", DateTimeOffset.UtcNow);

/// <summary>Raised when an outbound campaign changes status (e.g. running → paused).</summary>
public sealed record CampaignStatusChangedEvent(
    string TenantId, long CampaignId, string CampaignName,
    string OldStatus, string NewStatus)
    : PlatformEvent(TenantId, "campaign.status_changed", DateTimeOffset.UtcNow);

/// <summary>Raised when campaign dialing metrics are updated.</summary>
public sealed record CampaignMetricsUpdatedEvent(
    string TenantId, long CampaignId,
    int ContactsDialed, int ContactsRemaining,
    double ConnectRate, double AbandonRate, int ActiveCalls)
    : PlatformEvent(TenantId, "campaign.metrics_updated", DateTimeOffset.UtcNow);

/// <summary>Raised when an agent submits a disposition for a campaign call.</summary>
public sealed record CampaignDispositionSubmittedEvent(
    string TenantId, long CampaignId,
    string DispositionCode, string AgentId)
    : PlatformEvent(TenantId, "campaign.disposition_submitted", DateTimeOffset.UtcNow);

// ─── Agent Assist Events ──────────────────────────────────────────────────────

/// <summary>Raised when an agent assist suggestion is generated for a live call.</summary>
public sealed record AgentAssistSuggestionEvent(
    string TenantId,
    string SessionId,
    string AgentId,
    string SuggestionId,
    string Text,
    string Priority,
    string Source,
    string? TriggerPhrase)
    : PlatformEvent(TenantId, "agentassist.suggestion", DateTimeOffset.UtcNow);

/// <summary>Raised when a sentiment reading is produced for a live call segment.</summary>
public sealed record AgentAssistSentimentEvent(
    string TenantId,
    string SessionId,
    string AgentId,
    string Speaker,
    float Score,
    string Label,
    IReadOnlyList<string> TriggerWords)
    : PlatformEvent(TenantId, "agentassist.sentiment", DateTimeOffset.UtcNow);

/// <summary>Raised when a compliance rule violation is detected during a live call.</summary>
public sealed record AgentAssistComplianceAlertEvent(
    string TenantId,
    string SessionId,
    string AgentId,
    string RuleId,
    string? Phrase,
    string Severity)
    : PlatformEvent(TenantId, "agentassist.compliance_alert", DateTimeOffset.UtcNow);

/// <summary>Raised when a transcript segment is produced during a live call.</summary>
public sealed record AgentAssistTranscriptEvent(
    string TenantId,
    string SessionId,
    string AgentId,
    string Speaker,
    string Text,
    bool IsFinal)
    : PlatformEvent(TenantId, "agentassist.transcript", DateTimeOffset.UtcNow);
