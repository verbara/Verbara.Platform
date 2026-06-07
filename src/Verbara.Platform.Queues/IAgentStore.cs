using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public interface IAgentStore
{
    Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct);
    Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct);
    Task SaveAsync(Agent agent, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);

    // W3 — reaper enumeration of every routable agent ({Available, Busy}) across
    // ALL tenants, unpaged/streamed. Used by the liveness reaper to reconcile
    // "Postgres says routable" against "Redis says alive".
    IAsyncEnumerable<Agent> StreamRoutableAgentsAsync(CancellationToken ct);

    // W4 — drain-sweep enumeration of every agent with a pending pause, across ALL
    // tenants, unpaged/streamed. Mirrors StreamRoutableAgentsAsync.
    IAsyncEnumerable<Agent> StreamPendingPauseAgentsAsync(CancellationToken ct);

    // W5 — drain-sweep enumeration of every Offline agent across ALL tenants,
    // unpaged/streamed. Mirrors StreamRoutableAgentsAsync. The failover sweep starts
    // here (offline owners are the orphan candidates) then lists their active work.
    IAsyncEnumerable<Agent> StreamOfflineAgentsAsync(CancellationToken ct);
}

public sealed record AgentQuery
{
    public AgentState? State { get; init; }
    public EntityId? TeamId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}
