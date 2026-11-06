using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// The record of an offer on a conversation: which agent it was made to, and when. An offer is accepted
/// or rejected only by the agent it was made to.
/// </summary>
public static class ConversationOffer
{
    /// <summary>Metadata key holding the id of the agent the conversation was offered to.</summary>
    public const string OfferedToKey = ConversationQuery.OfferedToMetadataKey;

    /// <summary>Metadata key holding when the offer was made (round-trip "O" format, UTC).</summary>
    public const string OfferedAtKey = "_offeredAt";

    /// <summary>
    /// Metadata key holding the agent last-agent routing prefers for this conversation's first offer
    /// (conversations-return-to-last-agent design D3). Written by routing before queue assignment, removed by the
    /// offer-state write of the first successful offer, whichever agent receives it.
    /// </summary>
    public const string PreferredAgentIdKey = "_preferredAgentId";

    /// <summary>
    /// Metadata key holding what became of the preference at the first offer: <see cref="PreferredOutcomeOfferedPreferred"/>
    /// or <see cref="PreferredOutcomeFallback"/>. Written in the same offer-state write that removes
    /// <see cref="PreferredAgentIdKey"/>, never rewritten afterwards.
    /// </summary>
    public const string PreferredAgentOutcomeKey = "_preferredAgentOutcome";

    /// <summary>The first offer went to the preferred agent.</summary>
    public const string PreferredOutcomeOfferedPreferred = LastAgentOutcomes.OfferedPreferred;

    /// <summary>The first offer went to another agent.</summary>
    public const string PreferredOutcomeFallback = LastAgentOutcomes.Fallback;

    /// <summary>
    /// Whether <paramref name="conversation"/> is an open offer made to <paramref name="agentId"/>. An
    /// offer that records no agent is made to no one.
    /// </summary>
    public static bool IsOfferedTo(Conversation conversation, EntityId agentId)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.State == ConversationState.Offered
            && conversation.Metadata.TryGetValue(OfferedToKey, out var offeredTo)
            && string.Equals(offeredTo, agentId.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live-stream audience of a <c>conversation.message</c> of <paramref name="conversation"/>
    /// (contact-history-opens-by-assignment, design D17): its owner when the owner kind is <c>Agent</c>, and the agent it
    /// is open-offered to when its state is <c>Offered</c>. A <see langword="null"/> conversation, or one with neither,
    /// has <see cref="ConversationAudience.None"/>. Pass the conversation as it is now, read after any ownership change
    /// the same operation made.
    /// </summary>
    public static ConversationAudience AudienceOf(Conversation? conversation)
    {
        if (conversation is null)
            return ConversationAudience.None;

        var owner = conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId } ? ownerId.Value : null;
        var offeredTo = conversation.State == ConversationState.Offered
            && conversation.Metadata.TryGetValue(OfferedToKey, out var offered)
            && !string.IsNullOrEmpty(offered)
                ? offered
                : null;
        return owner is null && offeredTo is null ? ConversationAudience.None : new ConversationAudience(owner, offeredTo);
    }

    /// <summary>Whether <paramref name="conversation"/> records an offer made to an agent other than <paramref name="agentId"/>.</summary>
    internal static bool IsOfferedToAnotherAgent(Conversation conversation, EntityId agentId) =>
        conversation.Metadata.TryGetValue(OfferedToKey, out var offeredTo)
        && !string.Equals(offeredTo, agentId.Value, StringComparison.Ordinal);
}
