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

    public Task<PagedResult<AuthEvent>> SearchAsync(string tenantId, AuthEventQuery query, CancellationToken ct)
    {
        var filtered = _items
            .Where(e => e.TenantId == tenantId)
            .Where(e => query.UserId == null || e.UserId == query.UserId)
            .Where(e => query.EventType == null || e.EventType == query.EventType)
            .Where(e => query.StartDate == null || e.CreatedAt >= query.StartDate)
            .Where(e => query.EndDate == null || e.CreatedAt <= query.EndDate)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<AuthEvent>(items, totalCount, query.Page, query.PageSize));
    }

    public Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        IReadOnlyList<AuthEvent> result = _items
            .Where(e => e.TenantId == tenantId && e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        // ConcurrentBag does not support removal — rebuild
        var toKeep = _items.Where(e => !(e.TenantId == tenantId && e.UserId == userId)).ToList();
        var deleted = _items.Count - toKeep.Count;

        // Clear and re-add (ConcurrentBag has no RemoveWhere)
        while (_items.TryTake(out _)) { }
        foreach (var item in toKeep)
            _items.Add(item);

        return Task.FromResult(deleted);
    }

    public Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var toKeep = _items.Where(e => !(e.TenantId == tenantId && e.CreatedAt < cutoff)).ToList();
        var deleted = _items.Count - toKeep.Count;

        while (_items.TryTake(out _)) { }
        foreach (var item in toKeep)
            _items.Add(item);

        return Task.FromResult(deleted);
    }
}
