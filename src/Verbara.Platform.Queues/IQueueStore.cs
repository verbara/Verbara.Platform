using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public interface IQueueStore
{
    Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);
    Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(Queue queue, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);
}
