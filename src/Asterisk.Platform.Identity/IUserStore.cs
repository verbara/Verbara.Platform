using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IUserStore
{
    Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct);
    Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct);
    Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);

    /// <summary>
    /// v1.14.3 — list users with an optional case-insensitive email substring
    /// filter (R5.5 P0 finding #5 fix). Pre-v1.14.3, the
    /// <c>/admin/users?email=</c> query parameter was silently dropped at the
    /// endpoint layer; admin tooling that needed to look up a user by email
    /// resorted to scanning the full page list. The default implementation
    /// here delegates to the unfiltered overload when <paramref name="email"/>
    /// is null/whitespace, so existing call sites keep working unchanged.
    /// </summary>
    Task<PagedResult<User>> ListAsync(
        TenantId tenantId,
        PagedQuery query,
        string? email,
        CancellationToken ct)
        => string.IsNullOrWhiteSpace(email)
            ? ListAsync(tenantId, query, ct)
            : throw new NotSupportedException(
                $"{GetType().Name} does not support filtering by email. Override IUserStore.ListAsync(TenantId, PagedQuery, string?, CancellationToken).");

    Task<IReadOnlyList<User>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> userIds, CancellationToken ct);
    Task SaveAsync(User user, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
