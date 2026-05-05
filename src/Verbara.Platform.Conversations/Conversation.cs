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
