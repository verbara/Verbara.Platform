using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>
/// supervisor-silent-listen (design D4): finds the owner agent's live leg of a voice call. The leg is the participant
/// with no <c>LeftAt</c> whose channel starts with <c>PJSIP/{tenantId}-agent-{ownerAgentId}-</c> — the answering queue
/// member, the A-leg of a click-to-dial or a rescued callback alike. Tenant-safe by construction: the prefix carries
/// the tenant. Resolved at start time and never persisted (it would go stale on a transfer).
/// </summary>
internal static class AgentLegResolver
{
    /// <summary>
    /// The uniqueid of the owner agent's live leg, or <see langword="null"/> when there is no session, no agent owner
    /// or no live leg of that agent in <paramref name="tenantId"/>.
    /// </summary>
    public static string? Resolve(string tenantId, ConversationOwner? owner, IReadOnlyList<SessionParticipant>? participants)
    {
        if (participants is null || participants.Count == 0)
            return null;
        if (owner is not { Kind: ConversationOwnerKind.Agent, OwnerId: { } agentId })
            return null;

        var prefix = AgentLegPrefix(tenantId, agentId);
        return participants
            .Where(p => p.LeftAt is null && p.Channel.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p.UniqueId)
            .LastOrDefault();
    }

    /// <summary>
    /// The agent's leg that took the call, whether or not it has left, or <see langword="null"/> when there is none.
    /// The callback-eval stamp reads it at <c>CallEnded</c>, when every leg already carries a <c>LeftAt</c>. The SDK
    /// never assigns <c>ParticipantRole.Agent</c> (its <c>InferRole</c> knows only Internal, Caller and Destination),
    /// so the channel <c>PJSIP/{tenantId}-agent-{agentId}-</c> is what identifies the agent's legs (#384).
    /// <para>
    /// A call can carry more than one leg of the same agent: a queue rings a member again after the call came back
    /// to it. The leg that took the call is the one that was up at <paramref name="connectedAt"/>, the SDK's first
    /// queue connect, which is also the connect that set the member interface the agent id comes from. A later ring
    /// joins after it and never counts. A call with no queue connect (a click-to-dial) has no second ring: its agent
    /// leg is the first one.
    /// </para>
    /// </summary>
    public static SessionParticipant? FindAgentLeg(
        string tenantId, EntityId? agentId, IReadOnlyList<SessionParticipant> participants, DateTimeOffset? connectedAt)
    {
        if (agentId is not { } id)
            return null;

        var prefix = AgentLegPrefix(tenantId, id);
        var legs = participants.Where(p => p.Channel.StartsWith(prefix, StringComparison.Ordinal));
        return connectedAt is { } at
            ? legs.LastOrDefault(p => p.JoinedAt <= at && (p.LeftAt is null || p.LeftAt >= at))
            : legs.FirstOrDefault();
    }

    private static string AgentLegPrefix(string tenantId, EntityId agentId) => $"PJSIP/{tenantId}-agent-{agentId.Value}-";
}
