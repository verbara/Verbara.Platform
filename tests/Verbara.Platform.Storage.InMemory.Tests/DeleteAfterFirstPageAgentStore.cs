using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// Widened-window control for the agent enumeration (licensed-agent-metering-follow-ups, design "Measure first",
/// L16): wraps a real <see cref="IAgentStore"/> and, right after the first page is read, deletes one agent of that
/// page from the inner store — the interleaving a concurrent delete produces between two page reads. It intercepts
/// both page reads (<see cref="ListAsync"/>, the row-offset page, and <see cref="ListPageAfterAsync"/>, the keyset
/// page), so the same control runs against whichever one <c>ListAllAsync</c> uses.
/// </summary>
/// <remarks>
/// Linked into Verbara.Platform.Storage.Postgres.Tests as well, so the control is the same on both stores.
/// </remarks>
internal sealed class DeleteAfterFirstPageAgentStore(IAgentStore inner) : IAgentStore
{
    private int _pagesRead;

    /// <summary>The agent deleted after the first page, or <see langword="null"/> before any page is read.</summary>
    public EntityId? DeletedAgentId { get; private set; }

    public async Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct)
    {
        var page = await inner.ListAsync(tenantId, query, ct);
        await DeleteAfterFirstPageAsync(tenantId, page.Items, ct);
        return page;
    }

    public async Task<IReadOnlyList<Agent>> ListPageAfterAsync(
        TenantId tenantId, EntityId? afterAgentId, int limit, CancellationToken ct)
    {
        var page = await inner.ListPageAfterAsync(tenantId, afterAgentId, limit, ct);
        await DeleteAfterFirstPageAsync(tenantId, page, ct);
        return page;
    }

    private async Task DeleteAfterFirstPageAsync(TenantId tenantId, IReadOnlyList<Agent> page, CancellationToken ct)
    {
        if (++_pagesRead != 1 || page.Count == 0)
            return;

        // The first agent of the page: a row-offset walk then shifts every later agent one position down, so the
        // first agent of the next page slides into the page already read and is never returned.
        var victim = page[0].AgentId;
        await inner.DeleteAsync(tenantId, victim, ct);
        DeletedAgentId = victim;
    }

    public Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
        inner.GetByIdAsync(tenantId, agentId, ct);

    public Task<IReadOnlyList<Agent>> GetByIdsAsync(
        TenantId tenantId, IReadOnlyCollection<EntityId> agentIds, CancellationToken ct) =>
        inner.GetByIdsAsync(tenantId, agentIds, ct);

    public Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct) =>
        inner.GetByUserIdAsync(tenantId, userId, ct);

    public Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct) =>
        inner.GetByExtensionAsync(tenantId, extension, ct);

    public Task SaveAsync(Agent agent, CancellationToken ct) => inner.SaveAsync(agent, ct);

    public Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
        inner.DeleteAsync(tenantId, agentId, ct);

    public IAsyncEnumerable<Agent> StreamRoutableAgentsAsync(CancellationToken ct) =>
        inner.StreamRoutableAgentsAsync(ct);

    public IAsyncEnumerable<Agent> StreamPendingPauseAgentsAsync(CancellationToken ct) =>
        inner.StreamPendingPauseAgentsAsync(ct);

    public IAsyncEnumerable<Agent> StreamOfflineAgentsAsync(CancellationToken ct) =>
        inner.StreamOfflineAgentsAsync(ct);
}
