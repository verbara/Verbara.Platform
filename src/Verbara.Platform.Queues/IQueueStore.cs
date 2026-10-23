using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public interface IQueueStore
{
    Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);

    /// <summary>
    /// The tenant's queue named exactly <paramref name="name"/>, or null. Queue names are not unique
    /// (<c>queue_configs</c> is keyed by <c>(tenant_id, queue_id)</c>), so an active queue wins over an inactive
    /// one of the same name, then the lowest queue id.
    /// </summary>
    Task<Queue?> GetByNameAsync(TenantId tenantId, string name, CancellationToken ct);
    Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);
    Task SaveAsync(Queue queue, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);
}
