using Verbara.Platform.Queues;

namespace Verbara.Platform.Routing.Inbound.Services;

/// <summary>
/// ADR-0026 Phase B — projection of an <see cref="Agent"/> together with the
/// per-(queue, agent) penalty surfaced by <c>queue_memberships</c>. Returned
/// by <see cref="IRoutingEligibilityService"/> so the selector can sort the
/// candidate pool ASC by penalty (0 = highest priority, matching Asterisk
/// <c>app_queue</c> semantics) without re-querying the membership store.
/// </summary>
/// <param name="Agent">The candidate agent.</param>
/// <param name="Penalty">Queue penalty from <c>queue_memberships.penalty</c>.</param>
public sealed record RoutableAgent(Agent Agent, int Penalty);
