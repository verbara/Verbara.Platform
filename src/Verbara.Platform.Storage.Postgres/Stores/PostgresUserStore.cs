using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.DataProtection;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUserStore : IUserStore
{
    /// <summary>
    /// DataProtection purpose for the TOTP shared-secret column. ADR-0003
    /// extension (same convention as
    /// <see cref="PostgresTenantAuthConfigStore.OidcClientSecretProtectorPurpose"/>):
    /// every column-level secret persistence path MUST go through
    /// <see cref="IDataProtectionProvider"/> with a concern-specific purpose
    /// string so each concern can rotate keys independently and cross-purpose
    /// decryption is impossible.
    /// <para>
    /// The literal is part of the persistence contract — every value already
    /// stored in <c>users.mfa_secret</c> was wrapped under it. Renaming it
    /// makes every value stored under the old purpose unreadable, so a rename
    /// requires a rewrap migration and is never a bare edit.
    /// </para>
    /// </summary>
    public const string MfaSecretProtectorPurpose = "Verbara.UserMfaSecret";

    /// <summary>
    /// DataProtection purpose for the recovery-code digest array. Held apart
    /// from <see cref="MfaSecretProtectorPurpose"/> because the two columns
    /// have genuinely different lifecycles — the secret is written once at
    /// enroll, the array is rewritten on every code redemption — and ADR-0003's
    /// convention is one purpose per concern.
    /// <para>
    /// The literal is part of the persistence contract — every element already
    /// stored in <c>users.mfa_recovery_codes</c> was wrapped under it.
    /// Renaming it makes every value stored under the old purpose unreadable,
    /// so a rename requires a rewrap migration and is never a bare edit.
    /// </para>
    /// </summary>
    public const string MfaRecoveryCodesProtectorPurpose = "Verbara.UserMfaRecoveryCodes";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _mfaSecretProtector;
    private readonly IDataProtector _mfaRecoveryCodesProtector;

    public PostgresUserStore(NpgsqlDataSource dataSource, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _dataSource = dataSource;
        _mfaSecretProtector = dataProtectionProvider.CreateProtector(MfaSecretProtectorPurpose);
        _mfaRecoveryCodesProtector = dataProtectionProvider.CreateProtector(MfaRecoveryCodesProtectorPurpose);
    }

    /// <summary>
    /// Writes the encrypted form so the raw Base32 TOTP secret never lands in
    /// the row. Null or empty in ⇒ <c>null</c> out, so a user who never
    /// enrolled keeps the column's "no MFA material" shape.
    /// </summary>
    internal string? ProtectMfaSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return _mfaSecretProtector.Protect(value);
    }

    /// <summary>
    /// Returns the unwrapped (plaintext) TOTP shared secret expected by
    /// callers (notably <c>MfaService.VerifyCode</c>, which feeds it straight
    /// into the TOTP computation). On <see cref="CryptographicException"/> —
    /// which means the row still holds the legacy unwrapped secret that the
    /// <c>UserMfaEncryptionMigrator</c> has not yet rewritten — the value is
    /// returned verbatim. The migrator runs on host startup and is idempotent;
    /// the catch is a transitional belt-and-suspenders guard so a request
    /// arriving between deploy and migrator completion doesn't fail.
    /// </summary>
    internal string? UnprotectMfaSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return _mfaSecretProtector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Legacy unwrapped row. Returning the raw value preserves TOTP
            // verification until the migrator catches up. Protect-on-write
            // ensures every NEW save is encrypted from this point forward.
            return value;
        }
    }

    /// <summary>
    /// Wraps each recovery-code digest individually so the column stays a
    /// <c>TEXT[]</c> of the same length (design D3). Elements are opaque: the
    /// column holds both legacy BCrypt digests and the wizard's SHA-256 hex
    /// digests, and neither is inspected, normalised, or re-hashed here.
    /// <c>null</c> in ⇒ <c>null</c> out and empty in ⇒ empty out, so the
    /// column keeps its shape.
    /// </summary>
    internal string[]? ProtectRecoveryCodes(IReadOnlyList<string>? values)
    {
        if (values is null)
            return null;
        if (values.Count == 0)
            return [];

        var wrapped = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
            wrapped[i] = _mfaRecoveryCodesProtector.Protect(values[i]);
        return wrapped;
    }

    /// <summary>
    /// Unwraps each element independently. A <see cref="CryptographicException"/>
    /// on one element means that element is still the legacy unwrapped digest
    /// the <c>UserMfaEncryptionMigrator</c> has not yet rewritten, so it is
    /// returned verbatim while its siblings still unwrap — a row interrupted
    /// mid-rewrite therefore projects correctly, preserving order and length
    /// (design D8). <c>null</c> in ⇒ <c>null</c> out and empty in ⇒ empty out.
    /// </summary>
    internal string[]? UnprotectRecoveryCodes(string[]? values)
    {
        if (values is null)
            return null;
        if (values.Length == 0)
            return [];

        var unwrapped = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            try
            {
                unwrapped[i] = _mfaRecoveryCodesProtector.Unprotect(values[i]);
            }
            catch (CryptographicException)
            {
                // Legacy unwrapped element. Returning it verbatim preserves
                // recovery-code redemption until the migrator catches up.
                unwrapped[i] = values[i];
            }
        }
        return unwrapped;
    }

    private const string SelectColumns =
        "user_id, tenant_id, email, display_name, role, status, created_at, updated_at, created_by, updated_by, " +
        "password_hash, mfa_enabled, mfa_secret, mfa_recovery_codes, mfa_confirmed_at, email_verified, " +
        "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id, oidc_subject";

    public async Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            UserRow.Map, ct);
        return row?.ToUser(this);
    }

    public async Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND lower(email) = lower(@Email)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Email", email));
            },
            UserRow.Map, ct);
        return row?.ToUser(this);
    }

    public async Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND oidc_subject = @OidcSubject",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("OidcSubject", oidcSubject));
            },
            UserRow.Map, ct);
        return row?.ToUser(this);
    }

    public Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
        => ListAsync(tenantId, query, email: null, ct);

    public async Task<PagedResult<User>> ListAsync(
        TenantId tenantId,
        PagedQuery query,
        string? email,
        CancellationToken ct)
    {
        // v1.14.3 (R5.5 P0 finding #5 fix). When `email` is supplied, filter
        // case-insensitively on a substring match using `idx_users_email`'s
        // lower(email) shape. The same WHERE clause feeds both COUNT and
        // SELECT so total + page stay consistent.
        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var emailPattern = hasEmail ? $"%{email!.Trim().ToLowerInvariant()}%" : null;

        int total;
        List<UserRow> rows;

        if (hasEmail)
        {
            total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM users WHERE tenant_id = @TenantId AND lower(email) LIKE @EmailPattern",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("EmailPattern", emailPattern!));
                },
                ct) ?? 0L);
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND lower(email) LIKE @EmailPattern " +
                "ORDER BY created_at LIMIT @Limit OFFSET @Offset",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("EmailPattern", emailPattern!));
                    p.Add(new NpgsqlParameter("Limit", query.PageSize));
                    p.Add(new NpgsqlParameter("Offset", query.Offset));
                },
                UserRow.Map, ct);
        }
        else
        {
            total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM users WHERE tenant_id = @TenantId",
                p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
                ct) ?? 0L);
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId " +
                "ORDER BY created_at LIMIT @Limit OFFSET @Offset",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("Limit", query.PageSize));
                    p.Add(new NpgsqlParameter("Offset", query.Offset));
                },
                UserRow.Map, ct);
        }

        var items = rows.Select(r => r.ToUser(this)).ToList();
        return new PagedResult<User>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<User>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return [];

        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND user_id = ANY(@Ids)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Ids", userIds.ToArray()));
            },
            UserRow.Map, ct);
        return rows.Select(r => r.ToUser(this)).ToList();
    }

    private const string InsertSql =
        "INSERT INTO users (user_id, tenant_id, email, display_name, role, status, created_at, updated_at, created_by, updated_by, " +
        "password_hash, mfa_enabled, mfa_secret, mfa_recovery_codes, mfa_confirmed_at, email_verified, " +
        "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id, oidc_subject) " +
        "VALUES (@UserId, @TenantId, @Email, @DisplayName, @Role, @Status, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, " +
        "@PasswordHash, @MfaEnabled, @MfaSecret, @MfaRecoveryCodes, @MfaConfirmedAt, @EmailVerified, " +
        "@FailedLoginAttempts, @LockedUntil, @PasswordChangedAt, @LastLoginAt, @AuthProvider, @ExternalId, @OidcSubject)";

    public async Task CreateAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        try
        {
            // A plain INSERT: no ON CONFLICT clause, so a write can never land on — or bring back —
            // a row it did not create. Every change to an existing user goes through a writer below.
            await _dataSource.ExecuteAsync(
                InsertSql,
                p =>
                {
                    p.Add(new NpgsqlParameter("UserId", user.UserId.Value));
                    p.Add(new NpgsqlParameter("TenantId", user.TenantId.Value));
                    p.Add(new NpgsqlParameter("Email", user.Email));
                    p.Add(new NpgsqlParameter("DisplayName", user.DisplayName));
                    p.Add(new NpgsqlParameter("Role", (int)user.Role));
                    p.Add(new NpgsqlParameter("Status", (int)user.Status));
                    p.Add(new NpgsqlParameter("CreatedAt", user.CreatedAt));
                    p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)user.UpdatedAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)user.CreatedBy ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)user.UpdatedBy ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("PasswordHash", NpgsqlDbType.Text) { Value = (object?)user.PasswordHash ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("MfaEnabled", user.MfaEnabled));
                    // A7 (encrypt-mfa-secrets-at-rest): wrap on write — never write
                    // plaintext MFA material. Both parameters stay explicitly typed
                    // because either can be DBNull.Value and an untyped nullable
                    // parameter throws 42P08.
                    p.Add(new NpgsqlParameter("MfaSecret", NpgsqlDbType.Text) { Value = (object?)ProtectMfaSecret(user.MfaSecret) ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("MfaRecoveryCodes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)ProtectRecoveryCodes(user.MfaRecoveryCodes) ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("MfaConfirmedAt", NpgsqlDbType.TimestampTz) { Value = (object?)user.MfaConfirmedAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("EmailVerified", user.EmailVerified));
                    p.Add(new NpgsqlParameter("FailedLoginAttempts", user.FailedLoginAttempts));
                    p.Add(new NpgsqlParameter("LockedUntil", NpgsqlDbType.TimestampTz) { Value = (object?)user.LockedUntil ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("PasswordChangedAt", NpgsqlDbType.TimestampTz) { Value = (object?)user.PasswordChangedAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("LastLoginAt", NpgsqlDbType.TimestampTz) { Value = (object?)user.LastLoginAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("AuthProvider", user.AuthProvider));
                    p.Add(new NpgsqlParameter("ExternalId", NpgsqlDbType.Text) { Value = (object?)user.ExternalId ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("OidcSubject", NpgsqlDbType.Text) { Value = (object?)user.OidcSubject ?? DBNull.Value });
                },
                ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // v1.14.3 (R5.5 P0 finding #4 fix). Both the primary key (tenant_id, user_id) and
            // `idx_users_email` — UNIQUE on (tenant_id, lower(email)) — raise 23505. Translate to a
            // domain exception so the endpoint can return a structured 409 Conflict instead of a 500
            // carrying the raw constraint name.
            var field = ex.ConstraintName?.Contains("email", StringComparison.OrdinalIgnoreCase) == true
                ? "email"
                : null;
            throw new EntityAlreadyExistsException("user", field, ex);
        }
    }

    private const string UpdateProfileSql =
        "UPDATE users SET " +
        "  display_name = COALESCE(@DisplayName, display_name), " +
        "  email_verified = COALESCE(@EmailVerified, email_verified), " +
        "  auth_provider = COALESCE(@AuthProvider, auth_provider), " +
        "  oidc_subject = COALESCE(@OidcSubject, oidc_subject), " +
        "  updated_at = @UpdatedAt " +
        "WHERE tenant_id = @TenantId AND user_id = @UserId";

    public async Task<bool> UpdateProfileAsync(
        TenantId tenantId, EntityId userId, UserProfileChange change, DateTimeOffset updatedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var rows = await _dataSource.ExecuteAsync(
            UpdateProfileSql,
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("DisplayName", NpgsqlDbType.Text) { Value = (object?)change.DisplayName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("EmailVerified", NpgsqlDbType.Boolean) { Value = (object?)change.EmailVerified ?? DBNull.Value });
                p.Add(new NpgsqlParameter("AuthProvider", NpgsqlDbType.Text) { Value = (object?)change.AuthProvider ?? DBNull.Value });
                p.Add(new NpgsqlParameter("OidcSubject", NpgsqlDbType.Text) { Value = (object?)change.OidcSubject ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt });
            },
            ct);
        return rows > 0;
    }

    // One statement writes the admin fields and returns the values it replaced. The sub-select locks
    // the row first, so under READ COMMITTED a concurrent writer's committed value is what comes back
    // as `previous`, never a value read before it — and the expectation, when there is one, is checked
    // against that same locked row. No row (no such user, or a failed expectation) → no result.
    private static readonly string UpdateAdminFieldsSql =
        "UPDATE users AS u SET " +
        "  display_name = COALESCE(@DisplayName, u.display_name), " +
        "  role = COALESCE(@Role, u.role), " +
        "  status = COALESCE(@Status, u.status), " +
        "  updated_at = @UpdatedAt, " +
        "  updated_by = COALESCE(@UpdatedBy, u.updated_by) " +
        "FROM (SELECT tenant_id, user_id, display_name, role, status FROM users " +
        "      WHERE tenant_id = @TenantId AND user_id = @UserId FOR UPDATE) AS previous " +
        "WHERE u.tenant_id = previous.tenant_id AND u.user_id = previous.user_id " +
        "  AND (NOT @CheckExpected OR (previous.display_name = @ExpectedDisplayName " +
        "       AND previous.role = @ExpectedRole AND previous.status = @ExpectedStatus)) " +
        "RETURNING previous.display_name AS previous_display_name, previous.role AS previous_role, " +
        "  previous.status AS previous_status, " +
        string.Join(", ", SelectColumns.Split(", ").Select(column => "u." + column));

    public async Task<AdminFieldsWriteResult> UpdateAdminFieldsAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var expected = change.Expected;
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            UpdateAdminFieldsSql,
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("DisplayName", NpgsqlDbType.Text) { Value = (object?)change.DisplayName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Role", NpgsqlDbType.Integer) { Value = change.Role is { } role ? (object)(int)role : DBNull.Value });
                p.Add(new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = change.Status is { } status ? (object)(int)status : DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)updatedBy ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CheckExpected", NpgsqlDbType.Boolean) { Value = expected is not null });
                p.Add(new NpgsqlParameter("ExpectedDisplayName", NpgsqlDbType.Text) { Value = (object?)expected?.DisplayName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ExpectedRole", NpgsqlDbType.Integer) { Value = expected is { } e1 ? (object)(int)e1.Role : DBNull.Value });
                p.Add(new NpgsqlParameter("ExpectedStatus", NpgsqlDbType.Integer) { Value = expected is { } e2 ? (object)(int)e2.Status : DBNull.Value });
            },
            AdminFieldsRow.Map, ct);

        if (row is not null)
            return AdminFieldsWriteResult.Written(
                new AdminFields(row.previous_display_name, (UserRole)row.previous_role, (UserStatus)row.previous_status),
                row.User.ToUser(this));

        if (expected is null)
            return AdminFieldsWriteResult.NotFound;

        // Nothing was written: tell a missing row from a failed expectation. Not atomic with the write
        // above, and it need not be — neither outcome changed anything.
        var exists = await _dataSource.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE tenant_id = @TenantId AND user_id = @UserId)",
            p => AddKey(p, tenantId, userId),
            ct);
        return exists ? AdminFieldsWriteResult.Stale : AdminFieldsWriteResult.NotFound;
    }

    private const string SetPasswordHashSql =
        "UPDATE users SET password_hash = @NewHash, password_changed_at = @ChangedAt, " +
        "  failed_login_attempts = CASE WHEN @ClearLockout THEN 0 ELSE failed_login_attempts END, " +
        "  locked_until = CASE WHEN @ClearLockout THEN NULL ELSE locked_until END " +
        "WHERE tenant_id = @TenantId AND user_id = @UserId " +
        "  AND (@ExpectedHash IS NULL OR password_hash = @ExpectedHash)";

    public async Task<bool> SetPasswordHashAsync(
        TenantId tenantId, EntityId userId, string newHash, DateTimeOffset changedAt, bool clearLockout,
        string? expectedCurrentHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(newHash);
        var rows = await _dataSource.ExecuteAsync(
            SetPasswordHashSql,
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("NewHash", NpgsqlDbType.Text) { Value = newHash });
                p.Add(new NpgsqlParameter("ChangedAt", NpgsqlDbType.TimestampTz) { Value = changedAt });
                p.Add(new NpgsqlParameter("ClearLockout", NpgsqlDbType.Boolean) { Value = clearLockout });
                p.Add(new NpgsqlParameter("ExpectedHash", NpgsqlDbType.Text) { Value = (object?)expectedCurrentHash ?? DBNull.Value });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> RehashPasswordAsync(
        TenantId tenantId, EntityId userId, string expectedHash, string newHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedHash);
        ArgumentException.ThrowIfNullOrEmpty(newHash);
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET password_hash = @NewHash " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND password_hash = @ExpectedHash",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("NewHash", NpgsqlDbType.Text) { Value = newHash });
                p.Add(new NpgsqlParameter("ExpectedHash", NpgsqlDbType.Text) { Value = expectedHash });
            },
            ct);
        return rows > 0;
    }

    // `failed_login_attempts + 1` on the right-hand side reads the row as it stands when this statement
    // gets it (after waiting for any concurrent writer), so concurrent failures never lose a count and
    // the one that reaches the threshold sets the lock.
    private const string RecordFailedSignInSql =
        "UPDATE users SET failed_login_attempts = failed_login_attempts + 1, " +
        "  locked_until = CASE WHEN failed_login_attempts + 1 >= @Threshold THEN @LockUntil ELSE locked_until END " +
        "WHERE tenant_id = @TenantId AND user_id = @UserId " +
        "RETURNING failed_login_attempts, locked_until";

    public async Task<FailedSignInResult?> RecordFailedSignInAsync(
        TenantId tenantId, EntityId userId, int threshold, DateTimeOffset lockUntil, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            RecordFailedSignInSql,
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("Threshold", NpgsqlDbType.Integer) { Value = threshold });
                p.Add(new NpgsqlParameter("LockUntil", NpgsqlDbType.TimestampTz) { Value = lockUntil });
            },
            FailedSignInRow.Map, ct);
        return row is null ? null : new FailedSignInResult(row.failed_login_attempts, row.locked_until);
    }

    public async Task<bool> ResetLockoutAsync(
        TenantId tenantId, EntityId userId, DateTimeOffset now, bool onlyIfUnlocked, CancellationToken ct)
    {
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET failed_login_attempts = 0, locked_until = NULL " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId " +
            "  AND (NOT @OnlyIfUnlocked OR locked_until IS NULL OR locked_until <= @Now)",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("OnlyIfUnlocked", NpgsqlDbType.Boolean) { Value = onlyIfUnlocked });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> SetLastLoginAtAsync(TenantId tenantId, EntityId userId, DateTimeOffset at, CancellationToken ct)
    {
        // GREATEST ignores NULL, so a first sign-in sets the column and a late write never moves it back.
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET last_login_at = GREATEST(last_login_at, @At) " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("At", NpgsqlDbType.TimestampTz) { Value = at });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> SetPendingMfaAsync(
        TenantId tenantId, EntityId userId, string secret, IReadOnlyList<string> recoveryCodeDigests,
        DateTimeOffset updatedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(recoveryCodeDigests);
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET mfa_secret = @MfaSecret, mfa_recovery_codes = @MfaRecoveryCodes, updated_at = @UpdatedAt " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND mfa_enabled = false",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("MfaSecret", NpgsqlDbType.Text) { Value = ProtectMfaSecret(secret)! });
                p.Add(new NpgsqlParameter("MfaRecoveryCodes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ProtectRecoveryCodes(recoveryCodeDigests)! });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> EnableMfaAsync(TenantId tenantId, EntityId userId, DateTimeOffset confirmedAt, CancellationToken ct)
    {
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET mfa_enabled = true, mfa_confirmed_at = @ConfirmedAt, updated_at = @ConfirmedAt " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND mfa_enabled = false AND mfa_secret IS NOT NULL",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("ConfirmedAt", NpgsqlDbType.TimestampTz) { Value = confirmedAt });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> ClearMfaAsync(
        TenantId tenantId, EntityId userId, bool clearLockout, DateTimeOffset updatedAt, CancellationToken ct)
    {
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET mfa_enabled = false, mfa_secret = NULL, mfa_recovery_codes = NULL, " +
            "  mfa_confirmed_at = NULL, updated_at = @UpdatedAt, " +
            "  failed_login_attempts = CASE WHEN @ClearLockout THEN 0 ELSE failed_login_attempts END, " +
            "  locked_until = CASE WHEN @ClearLockout THEN NULL ELSE locked_until END " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("ClearLockout", NpgsqlDbType.Boolean) { Value = clearLockout });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> SetRecoveryCodesAsync(
        TenantId tenantId, EntityId userId, IReadOnlyList<string> recoveryCodeDigests, DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodeDigests);
        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET mfa_recovery_codes = @MfaRecoveryCodes, updated_at = @UpdatedAt " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND mfa_enabled = true",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("MfaRecoveryCodes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ProtectRecoveryCodes(recoveryCodeDigests)! });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(
        TenantId tenantId, EntityId userId, string recoveryCodeDigest, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(recoveryCodeDigest);

        // Each element is wrapped with a fresh DataProtection nonce, so the stored element cannot be
        // computed from the digest: find it by unwrapping (a legacy element still unwrapped by the
        // migrator matches as itself), then remove exactly that stored value — only while it is still
        // there, so of two redemptions of one code the second removes nothing and fails.
        var stored = await _dataSource.QuerySingleOrDefaultAsync<string[]?>(
            "SELECT mfa_recovery_codes FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            p => AddKey(p, tenantId, userId),
            r => r.IsDBNull(0) ? null : r.GetFieldValue<string[]>(0),
            ct);
        if (stored is null)
            return false;

        var unwrapped = UnprotectRecoveryCodes(stored)!;
        var index = Array.IndexOf(unwrapped, recoveryCodeDigest);
        if (index < 0)
            return false;

        var rows = await _dataSource.ExecuteAsync(
            "UPDATE users SET mfa_recovery_codes = array_remove(mfa_recovery_codes, @Stored) " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND @Stored = ANY(mfa_recovery_codes)",
            p =>
            {
                AddKey(p, tenantId, userId);
                p.Add(new NpgsqlParameter("Stored", NpgsqlDbType.Text) { Value = stored[index] });
            },
            ct);
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var rows = await _dataSource.ExecuteAsync(
            "DELETE FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            p => AddKey(p, tenantId, userId),
            ct);
        return rows > 0;
    }

    private static void AddKey(NpgsqlParameterCollection p, TenantId tenantId, EntityId userId)
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
        p.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = userId.Value });
    }

    private sealed class AdminFieldsRow
    {
        public UserRow User { get; init; } = null!;
        public string previous_display_name { get; init; } = null!;
        public int previous_role { get; init; }
        public int previous_status { get; init; }

        public static AdminFieldsRow Map(NpgsqlDataReader r) => new()
        {
            User = UserRow.Map(r),
            previous_display_name = r.GetString("previous_display_name"),
            previous_role = r.GetInt32("previous_role"),
            previous_status = r.GetInt32("previous_status"),
        };
    }

    private sealed class FailedSignInRow
    {
        public int failed_login_attempts { get; init; }
        public DateTime? locked_until { get; init; }

        public static FailedSignInRow Map(NpgsqlDataReader r) => new()
        {
            failed_login_attempts = r.GetInt32("failed_login_attempts"),
            locked_until = r.GetDateTimeOrNull("locked_until"),
        };
    }

    private sealed class UserRow
    {
        public string user_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string email { get; init; } = null!;
        public string display_name { get; init; } = null!;
        public int role { get; init; }
        public int status { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }
        public string? password_hash { get; init; }
        public bool mfa_enabled { get; init; }
        public string? mfa_secret { get; init; }
        public string[]? mfa_recovery_codes { get; init; }
        public DateTime? mfa_confirmed_at { get; init; }
        public bool email_verified { get; init; }
        public int failed_login_attempts { get; init; }
        public DateTime? locked_until { get; init; }
        public DateTime? password_changed_at { get; init; }
        public DateTime? last_login_at { get; init; }
        public string auth_provider { get; init; } = null!;
        public string? external_id { get; init; }
        public string? oidc_subject { get; init; }

        public static UserRow Map(NpgsqlDataReader r) => new()
        {
            user_id = r.GetString("user_id"),
            tenant_id = r.GetString("tenant_id"),
            email = r.GetString("email"),
            display_name = r.GetString("display_name"),
            role = r.GetInt32("role"),
            status = r.GetInt32("status"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
            password_hash = r.GetStringOrNull("password_hash"),
            mfa_enabled = r.GetBoolean("mfa_enabled"),
            mfa_secret = r.GetStringOrNull("mfa_secret"),
            mfa_recovery_codes = r.IsDBNull(r.GetOrdinal("mfa_recovery_codes"))
                ? null
                : r.GetFieldValue<string[]>(r.GetOrdinal("mfa_recovery_codes")),
            mfa_confirmed_at = r.GetDateTimeOrNull("mfa_confirmed_at"),
            email_verified = r.GetBoolean("email_verified"),
            failed_login_attempts = r.GetInt32("failed_login_attempts"),
            locked_until = r.GetDateTimeOrNull("locked_until"),
            password_changed_at = r.GetDateTimeOrNull("password_changed_at"),
            last_login_at = r.GetDateTimeOrNull("last_login_at"),
            auth_provider = r.GetString("auth_provider"),
            external_id = r.GetStringOrNull("external_id"),
            oidc_subject = r.GetStringOrNull("oidc_subject"),
        };

        public User ToUser(PostgresUserStore store) => new()
        {
            UserId = EntityId.From(user_id),
            TenantId = new TenantId(tenant_id),
            Email = email,
            DisplayName = display_name,
            Role = (UserRole)role,
            Status = (UserStatus)status,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
            CreatedBy = created_by,
            UpdatedBy = updated_by,
            PasswordHash = password_hash,
            MfaEnabled = mfa_enabled,
            // A7 (encrypt-mfa-secrets-at-rest): unwrap on read — internal
            // callers (MfaService TOTP verification, recovery-code redemption)
            // receive the original values; the API layer never projects either
            // field, so they don't cross the HTTP boundary. The unwrap lives
            // here and not in Map because Map MUST stay static to satisfy the
            // Verbara.Sdk.Data.Npgsql mapper delegate (design D7).
            MfaSecret = store.UnprotectMfaSecret(mfa_secret),
            MfaRecoveryCodes = store.UnprotectRecoveryCodes(mfa_recovery_codes),
            MfaConfirmedAt = mfa_confirmed_at,
            EmailVerified = email_verified,
            FailedLoginAttempts = failed_login_attempts,
            LockedUntil = locked_until,
            PasswordChangedAt = password_changed_at,
            LastLoginAt = last_login_at,
            AuthProvider = auth_provider,
            ExternalId = external_id,
            OidcSubject = oidc_subject,
        };
    }
}
