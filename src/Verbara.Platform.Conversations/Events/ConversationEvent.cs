using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Events;

public abstract record ConversationEvent
{
    public required EntityId ConversationId { get; init; }
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed record ConversationCreatedEvent : ConversationEvent
{
    public required EntityId ContactId { get; init; }
    public required ChannelType InitialChannel { get; init; }
}

public sealed record ConversationStateChangedEvent : ConversationEvent
{
    public required ConversationState PreviousState { get; init; }
    public required ConversationState NewState { get; init; }
}

public sealed record ConversationAssignedEvent : ConversationEvent
{
    public required ConversationOwner NewOwner { get; init; }
    public ConversationOwner? PreviousOwner { get; init; }
}

public sealed record ConversationClosedEvent : ConversationEvent
{
    public required ConversationState FinalState { get; init; }
}
