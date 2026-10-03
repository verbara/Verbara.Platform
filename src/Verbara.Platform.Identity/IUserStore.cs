using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

/// <summary>
/// The users store. Only <see cref="CreateAsync"/> inserts a row. Every other write is an update of
/// an existing row that writes only its own columns, in one statement, with its precondition in the
/// same statement: a writer holding a user read earlier can neither bring back a deleted user nor
/// put back a password, MFA state, lock, failed-attempt count, role or status another writer changed
/// in between. Implementations hand out copies, as a database read does.
/// </summary>
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
    /// Inserts <paramref name="user"/> with every field. The only write that creates a row.
    /// </summary>
    /// <exception cref="EntityAlreadyExistsException">
    /// The user id, or the email (case-insensitive, per tenant), is already taken; nothing is written.
    /// </exception>
    Task CreateAsync(User user, CancellationToken ct);

    /// <summary>
    /// Writes the non-null fields of <paramref name="change"/>, and <see cref="User.UpdatedAt"/>, on an
    /// existing user. Touches no credential, MFA, lockout, role or status column.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user; nothing is created.</returns>
    Task<bool> UpdateProfileAsync(
        TenantId tenantId, EntityId userId, UserProfileChange change, DateTimeOffset updatedAt, CancellationToken ct);

    /// <summary>
    /// Writes the non-null fields of <paramref name="change"/> (display name, role, status), with
    /// <see cref="User.UpdatedAt"/> and <see cref="User.UpdatedBy"/>, in one statement on an existing
    /// user — and, when <see cref="AdminFieldsChange.Expected"/> is set, only while the row still holds
    /// those values. The only writer of role and status.
    /// </summary>
    /// <returns>
    /// <see cref="AdminFieldsWriteOutcome.Written"/> with the values the write replaced (read under the
    /// row lock) and the user as stored after it; <see cref="AdminFieldsWriteOutcome.NotFound"/> when
    /// there is no such user; <see cref="AdminFieldsWriteOutcome.Stale"/> when the expectation failed.
    /// </returns>
    Task<AdminFieldsWriteResult> UpdateAdminFieldsAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct);

    /// <summary>
    /// Sets the password hash and <see cref="User.PasswordChangedAt"/>; with
    /// <paramref name="clearLockout"/>, also zeroes the failed-attempt count and lifts any lock. With
    /// <paramref name="expectedCurrentHash"/>, writes only while that is still the stored hash, so a
    /// change verified against a password that has since been replaced does not land.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user or the stored hash differs.</returns>
    Task<bool> SetPasswordHashAsync(
        TenantId tenantId, EntityId userId, string newHash, DateTimeOffset changedAt, bool clearLockout,
        string? expectedCurrentHash, CancellationToken ct);

    /// <summary>
    /// Replaces <paramref name="expectedHash"/> with <paramref name="newHash"/> (the same password,
    /// hashed again) only while <paramref name="expectedHash"/> is still the stored hash. A rehash
    /// computed before a password change therefore never puts the old password back.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user or the stored hash differs.</returns>
    Task<bool> RehashPasswordAsync(
        TenantId tenantId, EntityId userId, string expectedHash, string newHash, CancellationToken ct);

    /// <summary>
    /// Adds one failed sign-in to the stored count, atomically, and locks the account until
    /// <paramref name="lockUntil"/> when the new count reaches <paramref name="threshold"/>.
    /// </summary>
    /// <returns>The count and lock now stored, or <see langword="null"/> when there is no such user.</returns>
    Task<FailedSignInResult?> RecordFailedSignInAsync(
        TenantId tenantId, EntityId userId, int threshold, DateTimeOffset lockUntil, CancellationToken ct);

    /// <summary>
    /// Zeroes the failed-attempt count and lifts the lock. With <paramref name="onlyIfUnlocked"/>, does
    /// nothing while a lock is still in force at <paramref name="now"/>: a reset for a successful
    /// sign-in written after a lock was set must not lift it.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user, or a lock in force was kept.</returns>
    Task<bool> ResetLockoutAsync(
        TenantId tenantId, EntityId userId, DateTimeOffset now, bool onlyIfUnlocked, CancellationToken ct);

    /// <summary>Moves <see cref="User.LastLoginAt"/> forward to <paramref name="at"/>, never back.</summary>
    /// <returns><see langword="false"/> when there is no such user.</returns>
    Task<bool> SetLastLoginAtAsync(TenantId tenantId, EntityId userId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Stores a pending TOTP secret and recovery-code digests for an enrollment in progress, only
    /// while MFA is not enabled: an enrolled factor is replaced only after it is disabled.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user or MFA is enabled.</returns>
    Task<bool> SetPendingMfaAsync(
        TenantId tenantId, EntityId userId, string secret, IReadOnlyList<string> recoveryCodeDigests,
        DateTimeOffset updatedAt, CancellationToken ct);

    /// <summary>
    /// Turns MFA on and stamps <see cref="User.MfaConfirmedAt"/>, only while it is off and a pending
    /// secret is stored.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user, MFA is on, or no secret is stored.</returns>
    Task<bool> EnableMfaAsync(TenantId tenantId, EntityId userId, DateTimeOffset confirmedAt, CancellationToken ct);

    /// <summary>
    /// Turns MFA off and clears the secret, recovery codes and confirmation; with
    /// <paramref name="clearLockout"/>, also zeroes the failed-attempt count and lifts any lock.
    /// </summary>
    /// <returns><see langword="false"/> when there is no such user.</returns>
    Task<bool> ClearMfaAsync(
        TenantId tenantId, EntityId userId, bool clearLockout, DateTimeOffset updatedAt, CancellationToken ct);

    /// <summary>Replaces the recovery-code digests, only while MFA is enabled.</summary>
    /// <returns><see langword="false"/> when there is no such user or MFA is not enabled.</returns>
    Task<bool> SetRecoveryCodesAsync(
        TenantId tenantId, EntityId userId, IReadOnlyList<string> recoveryCodeDigests, DateTimeOffset updatedAt,
        CancellationToken ct);

    /// <summary>
    /// Removes <paramref name="recoveryCodeDigest"/> (an element of <see cref="User.MfaRecoveryCodes"/>
    /// as read) from the stored digests, only while it is still there: of two redemptions of one code,
    /// exactly one succeeds.
    /// </summary>
    /// <returns><see langword="true"/> when this call removed it.</returns>
    Task<bool> ConsumeRecoveryCodeAsync(
        TenantId tenantId, EntityId userId, string recoveryCodeDigest, CancellationToken ct);

    /// <summary>Deletes the user.</summary>
    /// <returns><see langword="true"/> when a user was deleted.</returns>
    Task<bool> DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
