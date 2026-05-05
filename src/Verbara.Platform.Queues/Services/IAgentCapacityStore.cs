using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

public interface IAgentCapacityStore
{
    Task<IReadOnlyList<AgentCapacityRecord>> ListByTenantAsync(TenantId tenantId, CancellationToken ct);
    Task UpsertAsync(AgentCapacityRecord record, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task ClearAllAsync(CancellationToken ct);
}
