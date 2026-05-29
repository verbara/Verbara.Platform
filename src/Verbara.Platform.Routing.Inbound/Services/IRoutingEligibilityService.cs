using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound.Services;

/// <summary>
/// ADR-0026 Phase B — produces the queue + channel-aware candidate pool for
/// agent selection. Combines <see cref="Verbara.Platform.Queues.Services.IAgentPresenceService"/>
/// (routable + capacity + skill match) with the <c>queue_memberships</c>
/// membership gate (member + not excluded + <c>AllowedChannels</c> permits
/// the conversation channel) and sorts ASC by penalty. Returned to
/// <see cref="RoundRobinAgentSelector"/> in place of the raw presence list.
/// </summary>
/// <remarks>
/// Membership executive routing parity with Asterisk: a Customer-tenant
/// agent without a <c>queue_memberships</c> row for the target queue is
/// NOT a candidate for any channel (digital + voice). Phase A.6 made the
/// model channel-aware; Phase B makes it executive across all channels.
/// </remarks>
public interface IRoutingEligibilityService
{
    /// <summary>
    /// Returns the ordered candidate pool for the given (tenant, queue, channel).
    /// First by penalty ASC (Asterisk app_queue semantics: 0 = highest priority),
    /// then by stable agent identity within the same penalty group so callers
    /// can layer round-robin counters on top.
    /// </summary>
    Task<IReadOnlyList<RoutableAgent>> GetEligibleAgentsAsync(
        TenantId tenantId,
        EntityId queueId,
        ChannelType channel,
        CancellationToken ct);
}
