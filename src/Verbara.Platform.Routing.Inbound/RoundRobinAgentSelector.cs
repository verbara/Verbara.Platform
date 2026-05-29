using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound.Services;

namespace Verbara.Platform.Routing.Inbound;

/// <summary>
/// Round-robin selection over the membership-aware eligible pool from
/// <see cref="IRoutingEligibilityService"/>. ADR-0026 Phase B added two
/// semantics on top of the legacy presence-only round-robin:
/// <list type="bullet">
/// <item>
///   <description>
///   <b>Penalty-grouped round-robin</b>: the eligible pool is pre-sorted ASC
///   by <c>queue_memberships.penalty</c>. Agents with penalty 0 are tried
///   first; if there are multiple, round-robin rotates within that group
///   only. Higher-penalty agents are reached only when no lower-penalty
///   candidate exists in the current invocation. This matches Asterisk
///   <c>app_queue</c>'s priority semantics.
///   </description>
/// </item>
/// <item>
///   <description>
///   <b>Sticky / last-agent bypass</b>: when <c>LastAgentMiddleware</c>
///   surfaces a <c>preferredAgentId</c>, the selector returns the preferred
///   agent if they have membership in <i>any</i> active queue of the tenant
///   even if not in the current queue's eligible pool. CSAT (returning the
///   contact to the previous agent) intentionally overrides strict per-queue
///   membership. If the preferred agent has zero memberships across the
///   tenant, the bypass does NOT trigger and the regular selection applies.
///   </description>
/// </item>
/// </list>
/// </summary>
public sealed class RoundRobinAgentSelector : IAgentSelector
{
    private readonly IRoutingEligibilityService _eligibility;
    private readonly IQueueMembershipStore _membershipStore;
    private readonly IAgentPresenceService _presenceService;
    private int _counter;

    public RoundRobinAgentSelector(
        IRoutingEligibilityService eligibility,
        IQueueMembershipStore membershipStore,
        IAgentPresenceService presenceService)
    {
        _eligibility = eligibility;
        _membershipStore = membershipStore;
        _presenceService = presenceService;
    }

    public async Task<EntityId?> SelectAgentAsync(
        TenantId tenantId,
        EntityId queueId,
        ChannelType channel,
        EntityId? preferredAgentId,
        CancellationToken ct)
    {
        var pool = await _eligibility
            .GetEligibleAgentsAsync(tenantId, queueId, channel, ct)
            .ConfigureAwait(false);

        if (preferredAgentId.HasValue)
        {
            // First try in-pool (cheapest path — penalty-aware membership
            // covers the channel already).
            var preferredInPool = pool.FirstOrDefault(a => a.Agent.AgentId == preferredAgentId.Value);
            if (preferredInPool is not null)
                return preferredInPool.Agent.AgentId;

            // Sticky bypass: preferred agent may be a member of a different
            // queue. Honor sticky if they have ANY membership in the tenant
            // AND are reachable for this channel (routable + capacity). If
            // they have zero memberships across the tenant the bypass does
            // not apply — they are not eligible for routing at all.
            var preferredId = preferredAgentId.Value;
            var preferredMemberships = await _membershipStore
                .ListByAgentAsync(tenantId, preferredId, ct)
                .ConfigureAwait(false);
            if (preferredMemberships.Any(m => !m.IsExcluded))
            {
                // Use the presence service (no queue filter for sticky) to
                // confirm the preferred agent is reachable for the channel.
                var presenceForCurrentQueue = await _presenceService
                    .GetAvailableAgentsAsync(tenantId, queueId, channel, ct)
                    .ConfigureAwait(false);
                var preferredReachable = presenceForCurrentQueue
                    .FirstOrDefault(a => a.AgentId == preferredId);
                if (preferredReachable is not null)
                    return preferredReachable.AgentId;
            }
        }

        if (pool.Count == 0)
            return null;

        // Penalty-grouped round-robin: rotate within the lowest-penalty group
        // only. Higher-penalty agents are reached only when nobody at the
        // current penalty band is available in this invocation.
        var lowestPenalty = pool[0].Penalty;
        var lowestBandSize = 0;
        for (var i = 0; i < pool.Count && pool[i].Penalty == lowestPenalty; i++)
            lowestBandSize++;

        var index = Math.Abs(Interlocked.Increment(ref _counter) - 1) % lowestBandSize;
        return pool[index].Agent.AgentId;
    }
}
