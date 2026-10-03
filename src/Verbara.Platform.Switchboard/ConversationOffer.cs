using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// The record of an offer on a conversation: which agent it was made to, and when. An offer is accepted
/// or rejected only by the agent it was made to.
/// </summary>
public static class ConversationOffer
{
    /// <summary>Metadata key holding the id of the agent the conversation was offered to.</summary>
    public const string OfferedToKey = "_offeredTo";

    /// <summary>Metadata key holding when the offer was made (round-trip "O" format, UTC).</summary>
    public const string OfferedAtKey = "_offeredAt";

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

    /// <summary>Whether <paramref name="conversation"/> records an offer made to an agent other than <paramref name="agentId"/>.</summary>
    internal static bool IsOfferedToAnotherAgent(Conversation conversation, EntityId agentId) =>
        conversation.Metadata.TryGetValue(OfferedToKey, out var offeredTo)
        && !string.Equals(offeredTo, agentId.Value, StringComparison.Ordinal);
}
