using Verbara.Platform.Conversations;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// Writes a route's last-agent preference onto the conversation it routes (conversations-return-to-last-agent design
/// D3), on the webhook and the WebChat paths alike, in the metadata block that precedes <c>AssignToQueueAsync</c>.
/// </summary>
public static class RoutePreferenceStamp
{
    /// <summary>
    /// Makes <paramref name="conversation"/>'s pending preference the one <paramref name="route"/> carries: sets
    /// <see cref="ConversationOffer.PreferredAgentIdKey"/> to the route's preferred agent, or removes a key an earlier
    /// routing attempt left when the route has none (audit 2026-11-06 F2: a first attempt whose metadata save committed
    /// and whose queue assignment then threw leaves its key on a queued, owner-less row, which a retry with no
    /// preference must not leave for distribution). Returns whether it changed the row, so the caller persists the change
    /// even when the route carries no other metadata.
    /// </summary>
    public static bool Apply(Conversation conversation, RouteResult route)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(route);

        var pending = conversation.Metadata.TryGetValue(ConversationOffer.PreferredAgentIdKey, out var current) ? current : null;
        if (route.PreferredAgentId is { } agentId)
        {
            if (string.Equals(pending, agentId.Value, StringComparison.Ordinal))
                return false;
            conversation.SetMetadata(ConversationOffer.PreferredAgentIdKey, agentId.Value);
            return true;
        }

        if (pending is null)
            return false;
        conversation.RemoveMetadata(ConversationOffer.PreferredAgentIdKey);
        return true;
    }
}
