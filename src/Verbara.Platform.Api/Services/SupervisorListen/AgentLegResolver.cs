using Verbara.Platform.Conversations;
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

        var prefix = $"PJSIP/{tenantId}-agent-{agentId.Value}-";
        return participants
            .Where(p => p.LeftAt is null && p.Channel.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p.UniqueId)
            .LastOrDefault();
    }
}
