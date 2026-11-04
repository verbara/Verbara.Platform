using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public sealed record ConversationQuery
{
    /// <summary>
    /// The conversation metadata key that names the agent an open offer was made to. The Switchboard's
    /// <c>ConversationOffer.OfferedToKey</c> writes it; the stores read it for <see cref="VisibleToAgentId"/>.
    /// </summary>
    public const string OfferedToMetadataKey = "_offeredTo";

    public ConversationState? State { get; init; }
    public EntityId? ContactId { get; init; }
    public EntityId? CaseId { get; init; }

    /// <summary>The conversations an agent owns (owner kind <c>Agent</c> and this id), in any state.</summary>
    public EntityId? AssignedAgentId { get; init; }

    /// <summary>
    /// The conversations an agent may see without the contact branch of the read rule
    /// (contact-history-opens-by-assignment, design D4): owned by it (owner kind <c>Agent</c> and this id) in any
    /// state, or in <see cref="ConversationState.Offered"/> with an open offer to it
    /// (<see cref="OfferedToMetadataKey"/>). The other filters apply on top.
    /// </summary>
    public EntityId? VisibleToAgentId { get; init; }

    public ChannelType? Channel { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}
