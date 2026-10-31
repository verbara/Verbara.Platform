using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// Widened-window control for the queue enumeration, the same one <c>DeleteAfterFirstPageAgentStore</c> is for
/// agents: wraps a real <see cref="IQueueStore"/> and, right after the first page is read, deletes one queue of that
/// page from the inner store, the interleaving a concurrent delete produces between two page reads. It intercepts
/// both page reads (<see cref="ListAsync"/>, the row-offset page, and <see cref="ListPageAfterAsync"/>, the keyset
/// page), so the same control runs against whichever one the walk uses.
/// </summary>
/// <remarks>
/// Linked into Verbara.Platform.Storage.Postgres.Tests and Verbara.Platform.Api.Tests as well, so the control is the
/// same on both stores and through the wrap-up refresh.
/// </remarks>
internal sealed class DeleteAfterFirstPageQueueStore(IQueueStore inner) : IQueueStore
{
    private int _pagesRead;

    /// <summary>The queue deleted after the first page, or <see langword="null"/> before any page is read.</summary>
    public EntityId? DeletedQueueId { get; private set; }

    public async Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var page = await inner.ListAsync(tenantId, query, ct);
        await DeleteAfterFirstPageAsync(tenantId, page.Items, ct);
        return page;
    }

    public async Task<IReadOnlyList<Queue>> ListPageAfterAsync(
        TenantId tenantId, EntityId? afterQueueId, int limit, CancellationToken ct)
    {
        var page = await inner.ListPageAfterAsync(tenantId, afterQueueId, limit, ct);
        await DeleteAfterFirstPageAsync(tenantId, page, ct);
        return page;
    }

    private async Task DeleteAfterFirstPageAsync(TenantId tenantId, IReadOnlyList<Queue> page, CancellationToken ct)
    {
        if (++_pagesRead != 1 || page.Count == 0)
            return;

        // The first queue of the page: a row-offset walk then shifts every later queue one position down, so the
        // first queue of the next page slides into the page already read and is never returned.
        var victim = page[0].QueueId;
        await inner.DeleteAsync(tenantId, victim, ct);
        DeletedQueueId = victim;
    }

    public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct) =>
        inner.GetByIdAsync(tenantId, queueId, ct);

    public Task<Queue?> GetByNameAsync(TenantId tenantId, string name, CancellationToken ct) =>
        inner.GetByNameAsync(tenantId, name, ct);

    public Task SaveAsync(Queue queue, CancellationToken ct) => inner.SaveAsync(queue, ct);

    public Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct) =>
        inner.DeleteAsync(tenantId, queueId, ct);
}
