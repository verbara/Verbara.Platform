using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// ADR-0012 Ola-3 — best-effort Asterisk-realtime-sync decorator over <see cref="IAgentStore"/>.
/// Migrates the agent sync that previously lived as a <c>RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c>
/// Service-Locator resolve inside <c>AdminEndpoints</c> (Create/Update/Delete agent) into the
/// storage seam. A <see cref="SaveAsync"/> upserts the PJSIP endpoint/auth/AOR rows — ONLY when
/// the agent has both an <see cref="Agent.Extension"/> and a <see cref="Agent.SipPassword"/>
/// (replicating the AdminEndpoints :522 / :622 guard; a partially-provisioned agent has no valid
/// PJSIP identity to sync). A <see cref="DeleteAsync"/> removes them. Every sync is best-effort:
/// a throw is swallowed + logged (EventId 4130) so the store write still succeeds — the
/// <see cref="RealtimeReconciliationService"/> re-converges. Read + stream methods pass through.
/// </summary>
internal sealed class RealtimeSyncingAgentStore : IAgentStore
{
    private readonly IAgentStore _inner;
    private readonly IRealtimeSyncService _sync;
    private readonly ILogger<RealtimeSyncingAgentStore> _logger;

    public RealtimeSyncingAgentStore(
        IAgentStore inner,
        IRealtimeSyncService sync,
        ILogger<RealtimeSyncingAgentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _sync = sync;
        _logger = logger;
    }

    public Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
        => _inner.GetByIdAsync(tenantId, agentId, ct);

    public Task<IReadOnlyList<Agent>> GetByIdsAsync(TenantId tenantId, IReadOnlyCollection<EntityId> agentIds, CancellationToken ct)
        => _inner.GetByIdsAsync(tenantId, agentIds, ct);

    public Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => _inner.GetByUserIdAsync(tenantId, userId, ct);

    public Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct)
        => _inner.GetByExtensionAsync(tenantId, extension, ct);

    public Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct)
        => _inner.ListAsync(tenantId, query, ct);

    public IAsyncEnumerable<Agent> StreamRoutableAgentsAsync(CancellationToken ct)
        => _inner.StreamRoutableAgentsAsync(ct);

    public IAsyncEnumerable<Agent> StreamPendingPauseAgentsAsync(CancellationToken ct)
        => _inner.StreamPendingPauseAgentsAsync(ct);

    public IAsyncEnumerable<Agent> StreamOfflineAgentsAsync(CancellationToken ct)
        => _inner.StreamOfflineAgentsAsync(ct);

    public async Task SaveAsync(Agent agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await _inner.SaveAsync(agent, ct).ConfigureAwait(false);

        // AdminEndpoints :522 / :622 — only agents with a complete SIP identity
        // (extension + password) have PJSIP rows to sync. Skip otherwise.
        if (string.IsNullOrEmpty(agent.Extension) || string.IsNullOrEmpty(agent.SipPassword))
            return;

        try
        {
            await _sync.SyncAgentAsync(agent.TenantId.Value, agent.AgentId.Value, agent.DisplayName,
                agent.Extension, agent.SipPassword, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RealtimeSyncDeferralLog.Deferred(_logger, "SyncAgent", agent.AgentId.Value, agent.TenantId.Value, ex);
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await _inner.DeleteAsync(tenantId, agentId, ct).ConfigureAwait(false);

        try
        {
            await _sync.RemoveAgentAsync(tenantId.Value, agentId.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RealtimeSyncDeferralLog.Deferred(_logger, "RemoveAgent", agentId.Value, tenantId.Value, ex);
        }
    }
}
