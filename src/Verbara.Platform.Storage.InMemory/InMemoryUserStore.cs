using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

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

    public Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(u =>
            u.TenantId == tenantId &&
            string.Equals(u.OidcSubject, oidcSubject, StringComparison.Ordinal));

        return Task.FromResult(result);
    }

    public Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
        => ListAsync(tenantId, query, email: null, ct);

    public Task<PagedResult<User>> ListAsync(
        TenantId tenantId,
        PagedQuery query,
        string? email,
        CancellationToken ct)
    {
        var pool = _items.Values.Where(u => u.TenantId == tenantId);

        // v1.14.3 (R5.5 P0 finding #5 fix). Mirror PostgresUserStore's
        // case-insensitive substring match so the in-memory + integration
        // tests stay consistent with the production path.
        if (!string.IsNullOrWhiteSpace(email))
        {
            var needle = email.Trim();
            pool = pool.Where(u => u.Email is not null
                && u.Email.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = pool.ToList();
        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<User>(items, totalCount, query.Page, query.PageSize));
    }

    public Task<IReadOnlyList<User>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return Task.FromResult<IReadOnlyList<User>>([]);

        var idSet = new HashSet<string>(userIds, StringComparer.Ordinal);
        var result = _items.Values
            .Where(u => u.TenantId.Value == tenantId && idSet.Contains(u.UserId.Value))
            .ToList();
        return Task.FromResult<IReadOnlyList<User>>(result);
    }

    public Task SaveAsync(User user, CancellationToken ct)
    {
        // v1.14.3 — mirror the Postgres `idx_users_email` UNIQUE on
        // (tenant_id, lower(email)). Without this, the in-memory test path
        // would silently UPSERT-by-userid and never reach the 409 branch
        // that PostgresUserStore's PostgresException 23505 catch produces.
        // See R5.5 P0 finding #4 + EntityAlreadyExistsException.
        var existingByEmail = _items.Values.FirstOrDefault(u =>
            u.TenantId == user.TenantId &&
            u.UserId != user.UserId &&  // updates of the same user pass
            !string.IsNullOrEmpty(u.Email) &&
            u.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase));
        if (existingByEmail is not null)
            throw new EntityAlreadyExistsException("user", "email");

        _items[(user.TenantId, user.UserId)] = user;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, userId), out _);
        return Task.CompletedTask;
    }
}
