using System.Reactive.Linq;
using System.Reactive.Subjects;

using Asterisk.Platform.Core.Notifications;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;

namespace Asterisk.Platform.Core;

/// <summary>
/// Lightweight in-process event bus for broadcasting platform events to SSE subscribers.
/// v1.6.0: hybrid facade. Synchronous <see cref="Subject{T}"/> preserves the existing
/// <c>Publish</c>/<c>Events</c> contract used by 35 publishers, 4 legacy subscribers,
/// and 8 test fixtures. In parallel, every published event is forwarded to the injected
/// <see cref="IPushEventBus"/> (when present) so new Asterisk.Sdk.Push-native consumers
/// can observe the same stream with tenant/user filtering semantics.
/// </summary>
public sealed class PlatformEventBus : IDisposable
{
    private readonly Subject<PlatformEvent> _events = new();
    private readonly IPushEventBus? _pushBus;

    /// <summary>
    /// Parameterless constructor retained for test fixtures that instantiate the bus
    /// directly via <c>new PlatformEventBus()</c>. No Sdk.Push forwarding in this mode.
    /// </summary>
    public PlatformEventBus()
    {
    }

    /// <summary>
    /// DI-friendly constructor that also mirrors every publish onto the injected
    /// <see cref="IPushEventBus"/> so Asterisk.Sdk.Push subscribers receive the same
    /// events alongside the legacy <see cref="Events"/> observable.
    /// </summary>
    public PlatformEventBus(IPushEventBus pushBus)
    {
        ArgumentNullException.ThrowIfNull(pushBus);
        _pushBus = pushBus;
    }

    /// <summary>
    /// Observable stream of all platform events (synchronous, hot).
    /// </summary>
    public IObservable<PlatformEvent> Events => _events.AsObservable();

    /// <summary>
    /// Publishes an event to all subscribers. Legacy subscribers receive it synchronously;
    /// the Sdk.Push bus (if wired) receives an asynchronous copy.
    /// </summary>
    public void Publish(PlatformEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _events.OnNext(evt);

        if (_pushBus is not null)
        {
            // RxPushEventBus enqueues via bounded channel TryWrite → completes synchronously
            // under the default DropOldest strategy. Await to honor CA2012 and surface
            // enqueue-time exceptions.
            var pending = _pushBus.PublishAsync(evt);
            if (!pending.IsCompletedSuccessfully)
            {
                pending.AsTask().GetAwaiter().GetResult();
            }
        }
    }

    public void Dispose() => _events.Dispose();
}

/// <summary>
/// Base record for all platform events distributed via <see cref="PlatformEventBus"/>.
/// Inherits from <see cref="PushEvent"/> so events flow through <see cref="IPushEventBus"/>.
/// </summary>
public abstract record PlatformEvent(string TenantId, string Type, DateTimeOffset Timestamp) : PushEvent
{
    /// <inheritdoc />
    public override string EventType => Type;

    /// <summary>
    /// Computed metadata bridging platform-level fields into the Sdk.Push envelope.
    /// Default: tenant broadcast (no user target). Override in records that carry a
    /// specific recipient (e.g. <see cref="NotificationEvent"/>).
    /// </summary>
    public override PushEventMetadata Metadata => new(TenantId, UserId: null, Timestamp, CorrelationId: null);
}

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

/// <summary>Raised when a conversation is offered to an agent from a queue.</summary>
public sealed record ConversationOfferedEvent(
    string TenantId, string ConversationId, string AgentId, string QueueId)
    : PlatformEvent(TenantId, "conversation.offered", DateTimeOffset.UtcNow);

/// <summary>Raised when a conversation offer to an agent expires without acceptance.</summary>
public sealed record ConversationOfferExpiredEvent(
    string TenantId, string ConversationId, string AgentId)
    : PlatformEvent(TenantId, "conversation.offer_expired", DateTimeOffset.UtcNow);

/// <summary>Raised when a conversation is abandoned while waiting in a queue.</summary>
public sealed record ConversationAbandonedEvent(
    string TenantId, string ConversationId, string QueueId)
    : PlatformEvent(TenantId, "conversation.abandoned", DateTimeOffset.UtcNow);

/// <summary>Raised when an agent's channel capacity or load changes.</summary>
public sealed record AgentCapacityChangedEvent(
    string TenantId, string AgentId, string Channel,
    int CurrentLoad, int MaxLoad, bool CanAcceptVoice)
    : PlatformEvent(TenantId, "agent.capacity_changed", DateTimeOffset.UtcNow);

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

// ─── Notification Events ─────────────────────────────────────────────────────

/// <summary>
/// Raised when a notification is created and persisted for a user. This is the only
/// event that is user-targeted — <see cref="Metadata"/> carries the recipient's UserId
/// so the Sdk.Push <see cref="Asterisk.Sdk.Push.Delivery.DefaultDeliveryFilter"/>
/// routes it to the correct SSE stream.
/// </summary>
public sealed record NotificationEvent(
    string TenantId,
    string NotificationType,
    DateTimeOffset Timestamp,
    string NotificationId,
    string UserId,
    NotificationCategory Category,
    NotificationSeverity Severity,
    string Title,
    string Body,
    string? ActionUrl)
    : PlatformEvent(TenantId, "notification.created", Timestamp)
{
    /// <inheritdoc />
    public override PushEventMetadata Metadata => new(TenantId, UserId, Timestamp, CorrelationId: null);
}
