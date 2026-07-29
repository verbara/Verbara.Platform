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

    public async Task SaveAsync(User user, CancellationToken ct)
    {
        try
        {
            await _dataSource.ExecuteAsync(
                "INSERT INTO users (user_id, tenant_id, email, display_name, role, status, created_at, updated_at, created_by, updated_by, " +
                "password_hash, mfa_enabled, mfa_secret, mfa_recovery_codes, mfa_confirmed_at, email_verified, " +
                "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id, oidc_subject) " +
                "VALUES (@UserId, @TenantId, @Email, @DisplayName, @Role, @Status, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, " +
                "@PasswordHash, @MfaEnabled, @MfaSecret, @MfaRecoveryCodes, @MfaConfirmedAt, @EmailVerified, " +
                "@FailedLoginAttempts, @LockedUntil, @PasswordChangedAt, @LastLoginAt, @AuthProvider, @ExternalId, @OidcSubject) " +
                "ON CONFLICT (tenant_id, user_id) DO UPDATE SET " +
                "  display_name = EXCLUDED.display_name, role = EXCLUDED.role, status = EXCLUDED.status, " +
                "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by, " +
                "  password_hash = EXCLUDED.password_hash, mfa_enabled = EXCLUDED.mfa_enabled, " +
                "  mfa_secret = EXCLUDED.mfa_secret, mfa_recovery_codes = EXCLUDED.mfa_recovery_codes, " +
                "  mfa_confirmed_at = EXCLUDED.mfa_confirmed_at, email_verified = EXCLUDED.email_verified, " +
                "  failed_login_attempts = EXCLUDED.failed_login_attempts, locked_until = EXCLUDED.locked_until, " +
                "  password_changed_at = EXCLUDED.password_changed_at, last_login_at = EXCLUDED.last_login_at, " +
                "  auth_provider = EXCLUDED.auth_provider, external_id = EXCLUDED.external_id, " +
                "  oidc_subject = EXCLUDED.oidc_subject",
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
            // v1.14.3 (R5.5 P0 finding #4 fix). The UPSERT clause handles
            // (tenant_id, user_id) collisions, but `idx_users_email` is a
            // separate UNIQUE constraint on (tenant_id, lower(email)) — when
            // an admin posts a brand-new user with an existing email, that
            // index fires 23505 and bubbled to ASP.NET's default 500
            // problem-handler. Translate to a domain exception so the
            // endpoint can return a structured 409 Conflict.
            var field = ex.ConstraintName?.Contains("email", StringComparison.OrdinalIgnoreCase) == true
                ? "email"
                : null;
            throw new EntityAlreadyExistsException("user", field, ex);
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            ct);
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
