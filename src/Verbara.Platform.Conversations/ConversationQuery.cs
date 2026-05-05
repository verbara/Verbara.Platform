using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public sealed record ConversationQuery
{
    public ConversationState? State { get; init; }
    public EntityId? ContactId { get; init; }
    public EntityId? CaseId { get; init; }
    public EntityId? AssignedAgentId { get; init; }
    public ChannelType? Channel { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}
