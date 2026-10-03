using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// In-memory <see cref="IUserStore"/> holding <c>PostgresUserStore</c>'s contract: only
/// <see cref="CreateAsync"/> adds a user, every other write changes an existing one (and creates
/// nothing), and each write checks its precondition and applies its change in one step.
/// </summary>
/// <remarks>
/// Users are copied on the way in and on the way out, as Postgres materialises a fresh object on
/// every read: a caller changing the object it holds changes nothing stored, and a test holding a
/// copy read before another write sees exactly what a request on another node would. Writers never
/// change a stored object in place — they store a changed copy — so a reader copying concurrently
/// always copies a whole user.
/// </remarks>
internal sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), User> _items = new();
    private readonly Lock _writeGate = new();

    public Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, userId), out var item);
        return Task.FromResult(item?.Clone());
    }

    public Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(u =>
            u.TenantId == tenantId &&
            u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(result?.Clone());
    }

    public Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(u =>
            u.TenantId == tenantId &&
            string.Equals(u.OidcSubject, oidcSubject, StringComparison.Ordinal));

        return Task.FromResult(result?.Clone());
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
        var items = filtered.Skip(query.Offset).Take(query.PageSize).Select(u => u.Clone()).ToList();

        return Task.FromResult(new PagedResult<User>(items, totalCount, query.Page, query.PageSize));
    }

    public Task<IReadOnlyList<User>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return Task.FromResult<IReadOnlyList<User>>([]);

        var idSet = new HashSet<string>(userIds, StringComparer.Ordinal);
        var result = _items.Values
            .Where(u => u.TenantId.Value == tenantId && idSet.Contains(u.UserId.Value))
            .Select(u => u.Clone())
            .ToList();
        return Task.FromResult<IReadOnlyList<User>>(result);
    }

    public Task CreateAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_writeGate)
        {
            // Postgres raises 23505 for both the primary key and `idx_users_email` (UNIQUE on
            // (tenant_id, lower(email))); PostgresUserStore maps the email index to the "email" field.
            if (_items.ContainsKey((user.TenantId, user.UserId)))
                throw new EntityAlreadyExistsException("user", null);

            var emailTaken = _items.Values.Any(u =>
                u.TenantId == user.TenantId &&
                !string.IsNullOrEmpty(u.Email) &&
                u.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase));
            if (emailTaken)
                throw new EntityAlreadyExistsException("user", "email");

            _items[(user.TenantId, user.UserId)] = user.Clone();
        }

        return Task.CompletedTask;
    }

    public Task<bool> UpdateProfileAsync(
        TenantId tenantId, EntityId userId, UserProfileChange change, DateTimeOffset updatedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (change.DisplayName is not null)
                user.DisplayName = change.DisplayName;
            if (change.EmailVerified is { } emailVerified)
                user.EmailVerified = emailVerified;
            if (change.AuthProvider is not null)
                user.AuthProvider = change.AuthProvider;
            if (change.OidcSubject is not null)
                user.OidcSubject = change.OidcSubject;
            user.UpdatedAt = updatedAt;
            return true;
        }));
    }

    public Task<AdminFieldsWriteResult> UpdateAdminFieldsAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_writeGate)
        {
            if (!_items.TryGetValue((tenantId, userId), out var current))
                return Task.FromResult(AdminFieldsWriteResult.NotFound);

            var previous = new AdminFields(current.DisplayName, current.Role, current.Status);
            if (change.Expected is { } expected && expected != previous)
                return Task.FromResult(AdminFieldsWriteResult.Stale);

            var updated = current.Clone();
            if (change.DisplayName is not null)
                updated.DisplayName = change.DisplayName;
            if (change.Role is { } role)
                updated.Role = role;
            if (change.Status is { } status)
                updated.Status = status;
            updated.UpdatedAt = updatedAt;
            if (updatedBy is not null)
                updated.UpdatedBy = updatedBy;

            _items[(tenantId, userId)] = updated;
            return Task.FromResult(AdminFieldsWriteResult.Written(previous, updated.Clone()));
        }
    }

    public Task<bool> SetPasswordHashAsync(
        TenantId tenantId, EntityId userId, string newHash, DateTimeOffset changedAt, bool clearLockout,
        string? expectedCurrentHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(newHash);
        return Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (expectedCurrentHash is not null && !string.Equals(user.PasswordHash, expectedCurrentHash, StringComparison.Ordinal))
                return false;
            user.PasswordHash = newHash;
            user.PasswordChangedAt = changedAt;
            if (clearLockout)
                ClearLockout(user);
            return true;
        }));
    }

    public Task<bool> RehashPasswordAsync(
        TenantId tenantId, EntityId userId, string expectedHash, string newHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedHash);
        ArgumentException.ThrowIfNullOrEmpty(newHash);
        return Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (!string.Equals(user.PasswordHash, expectedHash, StringComparison.Ordinal))
                return false;
            user.PasswordHash = newHash;
            return true;
        }));
    }

    public Task<FailedSignInResult?> RecordFailedSignInAsync(
        TenantId tenantId, EntityId userId, int threshold, DateTimeOffset lockUntil, CancellationToken ct)
    {
        FailedSignInResult? result = null;
        TryUpdate(tenantId, userId, user =>
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= threshold)
                user.LockedUntil = lockUntil;
            result = new FailedSignInResult(user.FailedLoginAttempts, user.LockedUntil);
            return true;
        });
        return Task.FromResult(result);
    }

    public Task<bool> ResetLockoutAsync(
        TenantId tenantId, EntityId userId, DateTimeOffset now, bool onlyIfUnlocked, CancellationToken ct) =>
        Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (onlyIfUnlocked && user.LockedUntil is { } lockedUntil && lockedUntil > now)
                return false;
            ClearLockout(user);
            return true;
        }));

    public Task<bool> SetLastLoginAtAsync(TenantId tenantId, EntityId userId, DateTimeOffset at, CancellationToken ct) =>
        Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (user.LastLoginAt is null || user.LastLoginAt < at)
                user.LastLoginAt = at;
            return true;
        }));

    public Task<bool> SetPendingMfaAsync(
        TenantId tenantId, EntityId userId, string secret, IReadOnlyList<string> recoveryCodeDigests,
        DateTimeOffset updatedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(recoveryCodeDigests);
        return Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (user.MfaEnabled)
                return false;
            user.MfaSecret = secret;
            user.MfaRecoveryCodes = recoveryCodeDigests.ToArray();
            user.UpdatedAt = updatedAt;
            return true;
        }));
    }

    public Task<bool> EnableMfaAsync(TenantId tenantId, EntityId userId, DateTimeOffset confirmedAt, CancellationToken ct) =>
        Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (user.MfaEnabled || user.MfaSecret is null)
                return false;
            user.MfaEnabled = true;
            user.MfaConfirmedAt = confirmedAt;
            user.UpdatedAt = confirmedAt;
            return true;
        }));

    public Task<bool> ClearMfaAsync(
        TenantId tenantId, EntityId userId, bool clearLockout, DateTimeOffset updatedAt, CancellationToken ct) =>
        Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            user.MfaEnabled = false;
            user.MfaSecret = null;
            user.MfaRecoveryCodes = null;
            user.MfaConfirmedAt = null;
            user.UpdatedAt = updatedAt;
            if (clearLockout)
                ClearLockout(user);
            return true;
        }));

    public Task<bool> SetRecoveryCodesAsync(
        TenantId tenantId, EntityId userId, IReadOnlyList<string> recoveryCodeDigests, DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodeDigests);
        return Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (!user.MfaEnabled)
                return false;
            user.MfaRecoveryCodes = recoveryCodeDigests.ToArray();
            user.UpdatedAt = updatedAt;
            return true;
        }));
    }

    public Task<bool> ConsumeRecoveryCodeAsync(
        TenantId tenantId, EntityId userId, string recoveryCodeDigest, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(recoveryCodeDigest);
        return Task.FromResult(TryUpdate(tenantId, userId, user =>
        {
            if (user.MfaRecoveryCodes is not { } codes || !codes.Contains(recoveryCodeDigest, StringComparer.Ordinal))
                return false;
            // Postgres' array_remove drops every equal element; so does this.
            user.MfaRecoveryCodes = codes.Where(c => !string.Equals(c, recoveryCodeDigest, StringComparison.Ordinal)).ToArray();
            return true;
        }));
    }

    public Task<bool> DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        lock (_writeGate)
        {
            return Task.FromResult(_items.TryRemove((tenantId, userId), out _));
        }
    }

    // Applies `change` to a copy of an existing user and stores the copy — or, when `change` refuses
    // (returns false), stores nothing. A missing user is never created.
    private bool TryUpdate(TenantId tenantId, EntityId userId, Func<User, bool> change)
    {
        lock (_writeGate)
        {
            if (!_items.TryGetValue((tenantId, userId), out var current))
                return false;

            var updated = current.Clone();
            if (!change(updated))
                return false;

            _items[(tenantId, userId)] = updated;
            return true;
        }
    }

    private static void ClearLockout(User user)
    {
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
    }
}
