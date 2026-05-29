using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public interface IQueueMembershipStore
{
    Task<IReadOnlyList<QueueMembership>> ListByTenantAsync(TenantId tenantId, CancellationToken ct);
    Task<IReadOnlyList<QueueMembership>> ListByQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);
    Task<IReadOnlyList<QueueMembership>> ListByAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<QueueMembership?> GetAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct);
    Task SaveAsync(QueueMembership membership, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct);
    Task DeleteAllForQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);
    Task DeleteAllForAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
}
