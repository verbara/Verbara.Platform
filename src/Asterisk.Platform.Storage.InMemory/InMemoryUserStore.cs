using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), User> _items = new();

    public Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, userId), out var item);
        return Task.FromResult(item);
    }

    public Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(u =>
            u.TenantId == tenantId &&
            u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(result);
    }

    public Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(u => u.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<User>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(User user, CancellationToken ct)
    {
        _items[(user.TenantId, user.UserId)] = user;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, userId), out _);
        return Task.CompletedTask;
    }
}
