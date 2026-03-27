using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryAuthEventStore : IAuthEventStore
{
    private readonly ConcurrentBag<AuthEvent> _items = [];

    public Task SaveAsync(AuthEvent authEvent, CancellationToken ct)
    {
        _items.Add(authEvent);
        return Task.CompletedTask;
    }

    public Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct)
    {
        var filtered = _items
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var offset = (page - 1) * pageSize;
        var items = filtered.Skip(offset).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<AuthEvent>(items, totalCount, page, pageSize));
    }

    public Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct)
    {
        var filtered = _items
            .Where(e => e.TenantId == tenantId && e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var offset = (page - 1) * pageSize;
        var items = filtered.Skip(offset).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<AuthEvent>(items, totalCount, page, pageSize));
    }
}
