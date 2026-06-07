using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public sealed class Conversation : ITenantScoped, IAuditable
{
    public required EntityId ConversationId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId ContactId { get; init; }
    public required ChannelType Channel { get; init; }
    public ConversationOwner? Owner { get; set; }
    public required ConversationState State { get; set; }
    public EntityId? CaseId { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// For voice conversations: the Asterisk call-global <c>LinkedId</c> (shared by every
    /// channel of one physical call). Acts as the per-call idempotency correlation key
    /// (unique per tenant via a partial index, migration 027) so the leader-emit voice
    /// bridge never creates a duplicate Conversation across a leadership failover.
    /// <see langword="null"/> for every non-voice conversation. Settable (not init-only) because an
    /// OUTBOUND voice Conversation (3B.2d) is pre-created by the dial service BEFORE the live call
    /// exists, then the bridge stamps the real LinkedId on <c>CallConnected</c> (inbound sets it at
    /// creation). The stores persist it on UPDATE, so a later stamp is durable.
    /// </summary>
    public string? VoiceLinkedId { get; set; }

    /// <summary>
    /// W5 — queue ordering priority; lower sorts earlier. 0 = normal FIFO (by CreatedAt);
    /// failover re-queue sets -1 to jump to the front.
    /// </summary>
    public int QueuePriority { get; set; }

    private readonly Dictionary<string, string> _metadata = new();
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    private readonly List<IConversationSession> _sessions = [];
    public IReadOnlyList<IConversationSession> Sessions => _sessions;

    public void TransitionTo(ConversationState newState, DateTimeOffset? timestamp = null)
    {
        ConversationStateMachine.EnsureTransition(State, newState);
        State = newState;

        if (ConversationStateMachine.IsTerminal(newState))
            ClosedAt ??= timestamp ?? DateTimeOffset.UtcNow;
    }

    public void AddSession(IConversationSession session) =>
        _sessions.Add(session);

    public void SetMetadata(string key, string value) =>
        _metadata[key] = value;
}
