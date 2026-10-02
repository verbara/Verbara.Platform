using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

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

    /// <summary>
    /// Inserts <paramref name="user"/> with every field, or updates an existing user with every
    /// field except <see cref="User.Role"/> and <see cref="User.Status"/>, which keep their stored
    /// values.
    /// </summary>
    /// <remarks>
    /// Callers load a user, change their own fields and save the whole object, with no concurrency
    /// check. If this save wrote role and status, a caller holding an object read before an admin
    /// suspended or demoted the account would put the old value back — a failed sign-in recording
    /// its attempt is enough. Only <see cref="SetStatusAsync"/> and <see cref="SetRoleAsync"/>
    /// change them, so a role or status change sticks.
    /// </remarks>
    Task SaveAsync(User user, CancellationToken ct);

    /// <summary>
    /// Sets the account status of an existing user, and its <see cref="User.UpdatedAt"/>, and writes
    /// nothing else.
    /// </summary>
    /// <returns>
    /// The status the user held immediately before this write (equal to <paramref name="status"/>
    /// when nothing changed), or <see langword="null"/> when there is no such user.
    /// </returns>
    Task<UserStatus?> SetStatusAsync(
        TenantId tenantId, EntityId userId, UserStatus status, DateTimeOffset updatedAt, CancellationToken ct);

    /// <summary>
    /// Sets the role of an existing user, and its <see cref="User.UpdatedAt"/>, and writes nothing
    /// else.
    /// </summary>
    /// <returns>
    /// The role the user held immediately before this write, or <see langword="null"/> when there is
    /// no such user.
    /// </returns>
    Task<UserRole?> SetRoleAsync(
        TenantId tenantId, EntityId userId, UserRole role, DateTimeOffset updatedAt, CancellationToken ct);

    Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
