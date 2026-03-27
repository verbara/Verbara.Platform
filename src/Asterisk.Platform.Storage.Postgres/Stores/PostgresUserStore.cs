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
        "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id";

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
            "failed_login_attempts, locked_until, password_changed_at, last_login_at, auth_provider, external_id) " +
            "VALUES (@UserId, @TenantId, @Email, @DisplayName, @Role, @Status, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, " +
            "@PasswordHash, @MfaEnabled, @MfaSecret, @MfaRecoveryCodes, @MfaConfirmedAt, @EmailVerified, " +
            "@FailedLoginAttempts, @LockedUntil, @PasswordChangedAt, @LastLoginAt, @AuthProvider, @ExternalId) " +
            "ON CONFLICT (tenant_id, user_id) DO UPDATE SET " +
            "  display_name = EXCLUDED.display_name, role = EXCLUDED.role, status = EXCLUDED.status, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by, " +
            "  password_hash = EXCLUDED.password_hash, mfa_enabled = EXCLUDED.mfa_enabled, " +
            "  mfa_secret = EXCLUDED.mfa_secret, mfa_recovery_codes = EXCLUDED.mfa_recovery_codes, " +
            "  mfa_confirmed_at = EXCLUDED.mfa_confirmed_at, email_verified = EXCLUDED.email_verified, " +
            "  failed_login_attempts = EXCLUDED.failed_login_attempts, locked_until = EXCLUDED.locked_until, " +
            "  password_changed_at = EXCLUDED.password_changed_at, last_login_at = EXCLUDED.last_login_at, " +
            "  auth_provider = EXCLUDED.auth_provider, external_id = EXCLUDED.external_id",
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
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM users WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId.Value, UserId = userId.Value });
    }

    private sealed record UserRow(
        string user_id,
        string tenant_id,
        string email,
        string display_name,
        int role,
        int status,
        DateTimeOffset created_at,
        DateTimeOffset? updated_at,
        string? created_by,
        string? updated_by,
        string? password_hash,
        bool mfa_enabled,
        string? mfa_secret,
        string[]? mfa_recovery_codes,
        DateTimeOffset? mfa_confirmed_at,
        bool email_verified,
        int failed_login_attempts,
        DateTimeOffset? locked_until,
        DateTimeOffset? password_changed_at,
        DateTimeOffset? last_login_at,
        string auth_provider,
        string? external_id)
    {
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
        };
    }
}
