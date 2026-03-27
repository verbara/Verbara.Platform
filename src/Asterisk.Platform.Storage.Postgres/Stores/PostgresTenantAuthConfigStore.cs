using Dapper;
using Npgsql;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantAuthConfigStore : ITenantAuthConfigStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantAuthConfigStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TenantAuthConfigRow>(
            "SELECT tenant_id, mfa_policy, mfa_required_roles, password_min_length, password_require_uppercase, " +
            "password_require_number, password_require_special, lockout_threshold, lockout_duration_minutes, " +
            "session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, oidc_authority, " +
            "oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, updated_at " +
            "FROM tenant_auth_config WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
        return row?.ToTenantAuthConfig();
    }

    public async Task SaveAsync(TenantAuthConfig config, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_auth_config (tenant_id, mfa_policy, mfa_required_roles, password_min_length, " +
            "password_require_uppercase, password_require_number, password_require_special, lockout_threshold, " +
            "lockout_duration_minutes, session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, " +
            "oidc_authority, oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, updated_at) " +
            "VALUES (@TenantId, @MfaPolicy, @MfaRequiredRoles, @PasswordMinLength, @PasswordRequireUppercase, " +
            "@PasswordRequireNumber, @PasswordRequireSpecial, @LockoutThreshold, @LockoutDurationMinutes, " +
            "@SessionIdleTimeoutMinutes, @SessionAbsoluteTimeoutHours, @OidcEnabled, @OidcAuthority, " +
            "@OidcClientId, @OidcClientSecret, @OidcAutoCreateUsers, @OidcDefaultRole, @UpdatedAt) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  mfa_policy = EXCLUDED.mfa_policy, mfa_required_roles = EXCLUDED.mfa_required_roles, " +
            "  password_min_length = EXCLUDED.password_min_length, password_require_uppercase = EXCLUDED.password_require_uppercase, " +
            "  password_require_number = EXCLUDED.password_require_number, password_require_special = EXCLUDED.password_require_special, " +
            "  lockout_threshold = EXCLUDED.lockout_threshold, lockout_duration_minutes = EXCLUDED.lockout_duration_minutes, " +
            "  session_idle_timeout_minutes = EXCLUDED.session_idle_timeout_minutes, session_absolute_timeout_hours = EXCLUDED.session_absolute_timeout_hours, " +
            "  oidc_enabled = EXCLUDED.oidc_enabled, oidc_authority = EXCLUDED.oidc_authority, " +
            "  oidc_client_id = EXCLUDED.oidc_client_id, oidc_client_secret = EXCLUDED.oidc_client_secret, " +
            "  oidc_auto_create_users = EXCLUDED.oidc_auto_create_users, oidc_default_role = EXCLUDED.oidc_default_role, " +
            "  updated_at = EXCLUDED.updated_at",
            new
            {
                config.TenantId,
                config.MfaPolicy,
                MfaRequiredRoles = config.MfaRequiredRoles.ToArray(),
                config.PasswordMinLength,
                config.PasswordRequireUppercase,
                config.PasswordRequireNumber,
                config.PasswordRequireSpecial,
                config.LockoutThreshold,
                config.LockoutDurationMinutes,
                config.SessionIdleTimeoutMinutes,
                config.SessionAbsoluteTimeoutHours,
                config.OidcEnabled,
                config.OidcAuthority,
                config.OidcClientId,
                config.OidcClientSecret,
                config.OidcAutoCreateUsers,
                config.OidcDefaultRole,
                config.UpdatedAt,
            });
    }

    private sealed record TenantAuthConfigRow(
        string tenant_id,
        string mfa_policy,
        string[]? mfa_required_roles,
        int password_min_length,
        bool password_require_uppercase,
        bool password_require_number,
        bool password_require_special,
        int lockout_threshold,
        int lockout_duration_minutes,
        int session_idle_timeout_minutes,
        int session_absolute_timeout_hours,
        bool oidc_enabled,
        string? oidc_authority,
        string? oidc_client_id,
        string? oidc_client_secret,
        bool oidc_auto_create_users,
        string oidc_default_role,
        DateTimeOffset? updated_at)
    {
        public TenantAuthConfig ToTenantAuthConfig() => new()
        {
            TenantId = tenant_id,
            MfaPolicy = mfa_policy,
            MfaRequiredRoles = mfa_required_roles ?? [],
            PasswordMinLength = password_min_length,
            PasswordRequireUppercase = password_require_uppercase,
            PasswordRequireNumber = password_require_number,
            PasswordRequireSpecial = password_require_special,
            LockoutThreshold = lockout_threshold,
            LockoutDurationMinutes = lockout_duration_minutes,
            SessionIdleTimeoutMinutes = session_idle_timeout_minutes,
            SessionAbsoluteTimeoutHours = session_absolute_timeout_hours,
            OidcEnabled = oidc_enabled,
            OidcAuthority = oidc_authority,
            OidcClientId = oidc_client_id,
            OidcClientSecret = oidc_client_secret,
            OidcAutoCreateUsers = oidc_auto_create_users,
            OidcDefaultRole = oidc_default_role,
            UpdatedAt = updated_at,
        };
    }
}
