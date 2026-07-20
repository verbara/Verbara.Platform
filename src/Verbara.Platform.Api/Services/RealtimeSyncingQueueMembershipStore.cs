using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// ADR-0012 Ola-3 — best-effort Asterisk-realtime-sync decorator over <see cref="IQueueMembershipStore"/>.
/// Migrates the queue-member sync that previously lived as a
/// <c>RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c> Service-Locator resolve inside
/// <c>QueueMembersEndpoints</c> (Add/Remove/Update member) and <c>AdminEndpoints</c> (CreateAgent
/// membership loop) into the storage seam. A <see cref="SaveAsync"/> upserts the Asterisk
/// <c>queue_members</c> row (the SDK v2.6.0-pro voice-gate handles <see cref="QueueMembership.AllowedChannels"/>
/// uniformly); a <see cref="DeleteAsync"/> removes it. Bulk deletes (<see cref="DeleteAllForQueueAsync"/>,
/// <see cref="DeleteAllForAgentAsync"/>) do NOT per-member sync — the queue/agent removal path already
/// removes the parent Asterisk rows, and the reconciler re-converges, matching the pre-migration
/// endpoints. Every sync is best-effort: a throw is swallowed + logged (EventId 4130).
/// </summary>
/// <remarks>
/// <b>R3 (decoration-cycle avoidance).</b> <see cref="IRealtimeSyncService.AddQueueMemberAsync"/> needs
/// the queue NAME + the agent DISPLAY NAME, which are NOT on <see cref="QueueMembership"/>. This decorator
/// resolves them via the inner <see cref="IQueueStore"/> + <see cref="IAgentStore"/> — injected by KEY to
/// the UNDECORATED concrete stores (never the decorators), so a membership write never re-enters the queue
/// or agent decorator's own sync path. Mirrors <see cref="RealtimeReconciliationService"/>'s lookup exactly.
/// </remarks>
internal sealed class RealtimeSyncingQueueMembershipStore : IQueueMembershipStore
{
    private readonly IQueueMembershipStore _inner;
    private readonly IQueueStore _queues;
    private readonly IAgentStore _agents;
    private readonly IRealtimeSyncService _sync;
    private readonly ILogger<RealtimeSyncingQueueMembershipStore> _logger;

    public RealtimeSyncingQueueMembershipStore(
        IQueueMembershipStore inner,
        IQueueStore queues,
        IAgentStore agents,
        IRealtimeSyncService sync,
        ILogger<RealtimeSyncingQueueMembershipStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _queues = queues;
        _agents = agents;
        _sync = sync;
        _logger = logger;
    }

    public Task<IReadOnlyList<QueueMembership>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
        => _inner.ListByTenantAsync(tenantId, ct);

    public Task<IReadOnlyList<QueueMembership>> ListByQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
        => _inner.ListByQueueAsync(tenantId, queueId, ct);

    public Task<IReadOnlyList<QueueMembership>> ListByAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
        => _inner.ListByAgentAsync(tenantId, agentId, ct);

    public Task<QueueMembership?> GetAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
        => _inner.GetAsync(tenantId, queueId, agentId, ct);

    public async Task SaveAsync(QueueMembership membership, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(membership);

        await _inner.SaveAsync(membership, ct).ConfigureAwait(false);

        try
        {
            // Resolve queue name + agent display name from the UNDECORATED inners
            // (R3: keyed, never the decorators). Skip the sync when either is gone —
            // the reconciler + parent-removal paths keep Asterisk convergent.
            var queue = await _queues.GetByIdAsync(membership.TenantId, membership.QueueId, ct).ConfigureAwait(false);
            if (queue is null)
                return;
            var agent = await _agents.GetByIdAsync(membership.TenantId, membership.AgentId, ct).ConfigureAwait(false);
            if (agent is null)
                return;

            await _sync.AddQueueMemberAsync(
                membership.TenantId.Value, queue.Name, agent.AgentId.Value, agent.DisplayName,
                Math.Clamp(membership.Penalty, 0, 10),
                allowedChannels: membership.AllowedChannels, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RealtimeSyncDeferralLog.Deferred(
                _logger, "AddQueueMember", membership.QueueId.Value, membership.TenantId.Value, ex);
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        // Resolve the queue name BEFORE the delete (RemoveQueueMemberAsync is keyed by name).
        var queue = await _queues.GetByIdAsync(tenantId, queueId, ct).ConfigureAwait(false);

        await _inner.DeleteAsync(tenantId, queueId, agentId, ct).ConfigureAwait(false);

        if (queue is null)
            return;

        try
        {
            await _sync.RemoveQueueMemberAsync(tenantId.Value, queue.Name, agentId.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RealtimeSyncDeferralLog.Deferred(_logger, "RemoveQueueMember", queue.Name, tenantId.Value, ex);
        }
    }

    // Bulk deletes: no per-member sync. The queue/agent removal path removes the parent
    // Asterisk rows and the reconciler re-converges — matching the pre-migration endpoints
    // (AdminEndpoints DeleteQueue/DeleteAgent call DeleteAllFor* WITHOUT a per-member sync).
    public Task DeleteAllForQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
        => _inner.DeleteAllForQueueAsync(tenantId, queueId, ct);

    public Task DeleteAllForAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
        => _inner.DeleteAllForAgentAsync(tenantId, agentId, ct);
}
