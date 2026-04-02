using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUserStore : IUserStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "user_id, tenant_id, email, display_name, role, status, created_at, updated_at, created_by, updated_by, " +
        "password_hash, mfa_enabled, mfa_secret, mfa_recovery_codes, mfa_confirmed_at, email_verified, " +
        "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id, oidc_subject";

    public async Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId.Value, UserId = userId.Value });
        return row?.ToUser();
    }

    public async Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND lower(email) = lower(@Email)",
            new { TenantId = tenantId.Value, Email = email });
        return row?.ToUser();
    }

    public async Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId AND oidc_subject = @OidcSubject",
            new { TenantId = tenantId.Value, OidcSubject = oidcSubject });
        return row?.ToUser();
    }

    public async Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
        var rows = await conn.QueryAsync<UserRow>(
            $"SELECT {SelectColumns} FROM users WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset });
        var items = rows.Select(r => r.ToUser()).ToList();
        return new PagedResult<User>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(User user, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
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
            new
            {
                UserId = user.UserId.Value,
                TenantId = user.TenantId.Value,
                user.Email,
                user.DisplayName,
                Role = (int)user.Role,
                Status = (int)user.Status,
                user.CreatedAt,
                user.UpdatedAt,
                user.CreatedBy,
                user.UpdatedBy,
                user.PasswordHash,
                user.MfaEnabled,
                user.MfaSecret,
                MfaRecoveryCodes = user.MfaRecoveryCodes?.ToArray(),
                user.MfaConfirmedAt,
                user.EmailVerified,
                user.FailedLoginAttempts,
                user.LockedUntil,
                user.PasswordChangedAt,
                user.LastLoginAt,
                user.AuthProvider,
                user.ExternalId,
                user.OidcSubject,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId.Value, UserId = userId.Value });
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
        public User ToUser() => new()
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
            MfaSecret = mfa_secret,
            MfaRecoveryCodes = mfa_recovery_codes,
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
