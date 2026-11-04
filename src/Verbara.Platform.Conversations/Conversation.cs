using System.Text.Json.Serialization;
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

    /// <summary>
    /// conversations-close-after-inactivity (design D2): the time of the last customer-facing activity, the clock of
    /// the inactivity close. Written by the stores' claim statements only (an inbound message, an agent or bot send),
    /// with the platform's clock and monotonically. A new conversation starts it at <see cref="CreatedAt"/>, or at the
    /// value set here when it is born with a message; the whole-row save never writes it afterwards, so a stale copy
    /// cannot roll it back. Nullable on the entity only: the store never stores null. Off the wire (no API or Web
    /// change).
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>
    /// contact-history-opens-by-assignment (design D7): the last queue that owned this conversation. Written by the
    /// stores only, never by a caller: every whole-row save whose owner is a queue stores that queue, and any other save
    /// keeps the stored value, so it never clears (an assignment to an agent overwrites the queue owner, not this).
    /// <see langword="null"/> until a queue owns the conversation, and for rows written before migration 027 that no
    /// queue owned then. Off the wire: the <c>Conversation</c> shape does not change; the contact history reads it.
    /// </summary>
    [JsonIgnore]
    public EntityId? QueueId { get; set; }

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

    /// <summary>Removes a metadata key if present. Used to clear W5 failover markers on reassign.</summary>
    public void RemoveMetadata(string key) => _metadata.Remove(key);
}
