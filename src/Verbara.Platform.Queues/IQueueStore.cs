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

    /// <summary>
    /// Keyset page of this tenant's queues: the queues whose id sorts after <paramref name="afterQueueId"/> (every
    /// queue when it is <see langword="null"/>), in ascending <c>queue_id</c> order, at most
    /// <paramref name="limit"/>. The order is the store's own and is stable, which is all a keyset walk needs: a
    /// queue that exists for the whole walk is returned exactly once.
    /// </summary>
    Task<IReadOnlyList<Queue>> ListPageAfterAsync(TenantId tenantId, EntityId? afterQueueId, int limit, CancellationToken ct);
    Task SaveAsync(Queue queue, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct);
}

/// <summary>Helpers over <see cref="IQueueStore"/>.</summary>
public static class QueueStoreExtensions
{
    /// <summary>The page size <see cref="ListAllAsync"/> reads with.</summary>
    public const int EnumerationPageSize = 1000;

    /// <summary>
    /// Every queue of <paramref name="tenantId"/>, in ascending queue-id order, read in keyset pages of
    /// <see cref="EnumerationPageSize"/> (<see cref="IQueueStore.ListPageAfterAsync"/>) until a page comes back
    /// shorter than that, so no caller is silently capped at one page.
    /// </summary>
    /// <remarks>
    /// Each page starts after the last queue id already read, never at a row offset, so a queue deleted while the
    /// walk runs cannot shift another queue out of it, and names that are not unique cannot straddle a page boundary
    /// in an unstable order. A queue created during the walk may be missing; one deleted after its page was read is
    /// still returned.
    /// </remarks>
    public static async Task<IReadOnlyList<Queue>> ListAllAsync(
        this IQueueStore store, TenantId tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var all = new List<Queue>();
        EntityId? after = null;
        while (true)
        {
            var page = await store.ListPageAfterAsync(tenantId, after, EnumerationPageSize, ct).ConfigureAwait(false);
            all.AddRange(page);
            if (page.Count < EnumerationPageSize)
                return all;
            after = page[^1].QueueId;
        }
    }
}
