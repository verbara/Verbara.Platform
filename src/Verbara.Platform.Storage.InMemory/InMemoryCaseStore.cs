using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryCaseStore : ICaseStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Case> _items = new();

    public Task<Case?> GetByIdAsync(TenantId tenantId, EntityId caseId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, caseId), out var item);
        return Task.FromResult(item);
    }

    public Task<PagedResult<Case>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(c => c.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Case>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Case caseEntity, CancellationToken ct)
    {
        _items[(caseEntity.TenantId, caseEntity.CaseId)] = caseEntity;
        return Task.CompletedTask;
    }
}
