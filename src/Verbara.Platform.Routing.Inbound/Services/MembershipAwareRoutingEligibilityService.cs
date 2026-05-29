using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Routing.Inbound.Services;

/// <summary>
/// ADR-0026 Phase B default <see cref="IRoutingEligibilityService"/> impl.
/// Reads the baseline pool from <see cref="IAgentPresenceService"/> (routable
/// + capacity + skill match) and intersects it with <c>queue_memberships</c>
/// (member + not excluded + channel allowed). Sorts ASC by penalty.
/// </summary>
public sealed class MembershipAwareRoutingEligibilityService : IRoutingEligibilityService
{
    private readonly IAgentPresenceService _presenceService;
    private readonly IQueueMembershipStore _membershipStore;

    public MembershipAwareRoutingEligibilityService(
        IAgentPresenceService presenceService,
        IQueueMembershipStore membershipStore)
    {
        _presenceService = presenceService;
        _membershipStore = membershipStore;
    }

    public async Task<IReadOnlyList<RoutableAgent>> GetEligibleAgentsAsync(
        TenantId tenantId,
        EntityId queueId,
        ChannelType channel,
        CancellationToken ct)
    {
        // 1) Baseline pool from presence (routable + capacity + skill match).
        //    The presence service stays unchanged so other consumers (supervisor
        //    UI, status reporting) continue to work as before.
        var baseline = await _presenceService
            .GetAvailableAgentsAsync(tenantId, queueId, channel, ct)
            .ConfigureAwait(false);

        if (baseline.Count == 0)
            return [];

        // 2) Memberships for this queue (Phase A.6 ListByQueueAsync — already
        //    returns rows ordered by penalty then agent id, but we re-sort
        //    after the intersection because some baseline agents may drop).
        var memberships = await _membershipStore
            .ListByQueueAsync(tenantId, queueId, ct)
            .ConfigureAwait(false);

        if (memberships.Count == 0)
            return [];

        // 3) Index memberships by agent id for O(1) lookup during intersection.
        var membershipByAgent = new Dictionary<EntityId, QueueMembership>(memberships.Count);
        foreach (var m in memberships)
        {
            membershipByAgent[m.AgentId] = m;
        }

        // 4) Intersect: agent must (a) be a member, (b) IsExcluded=false,
        //    (c) AllowedChannels permits the conversation channel.
        var channelName = channel.ToString();
        var result = new List<RoutableAgent>(baseline.Count);
        foreach (var agent in baseline)
        {
            if (!membershipByAgent.TryGetValue(agent.AgentId, out var membership))
                continue;
            if (membership.IsExcluded)
                continue;
            if (!AllowsChannel(membership.AllowedChannels, channelName))
                continue;

            result.Add(new RoutableAgent(agent, membership.Penalty));
        }

        // 5) Sort ASC by penalty (Asterisk app_queue semantics: 0 = highest
        //    priority). Stable by agent id within the same penalty group so
        //    the caller's round-robin counter advances predictably.
        result.Sort(static (a, b) =>
        {
            var byPenalty = a.Penalty.CompareTo(b.Penalty);
            return byPenalty != 0
                ? byPenalty
                : string.CompareOrdinal(a.Agent.AgentId.Value, b.Agent.AgentId.Value);
        });

        return result;
    }

    /// <summary>
    /// Channel-aware membership semantics from Phase A.6: <c>null</c> means the
    /// agent is a member for all channels the queue accepts (pre-v2.6.0 implicit
    /// behavior). A populated list restricts membership to the listed channels
    /// only. Comparison is case-insensitive so PascalCase storage (Phase A.6
    /// stored <c>"WebChat"</c>, <c>"Voice"</c>, etc., matching
    /// <see cref="ChannelType"/>.<c>ToString()</c>) works regardless of input
    /// casing.
    /// </summary>
    private static bool AllowsChannel(IReadOnlyList<string>? allowedChannels, string channelName)
    {
        if (allowedChannels is null) return true;
        foreach (var c in allowedChannels)
        {
            if (string.Equals(c, channelName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
