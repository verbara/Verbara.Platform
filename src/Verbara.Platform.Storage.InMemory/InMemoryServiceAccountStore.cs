using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryServiceAccountStore : IServiceAccountStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), ServiceAccount> _items = new();

    public Task<ServiceAccount?> GetByIdAsync(TenantId tenantId, EntityId accountId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, accountId), out var item);
        return Task.FromResult(item);
    }

    public Task<PagedResult<ServiceAccount>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(a => a.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<ServiceAccount>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(ServiceAccount account, CancellationToken ct)
    {
        _items[(account.TenantId, account.AccountId)] = account;
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(TenantId tenantId, EntityId accountId, CancellationToken ct)
    {
        if (_items.TryGetValue((tenantId, accountId), out var account))
            account.IsActive = false;

        return Task.CompletedTask;
    }
}
