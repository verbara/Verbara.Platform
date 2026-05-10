using System.Security.Cryptography;
using Dapper;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantAuthConfigStore : ITenantAuthConfigStore
{
    /// <summary>
    /// DataProtection purpose for the OIDC client secret column. ADR-0003
    /// extension (ADMIN-001 fix): every column-level secret persistence path
    /// MUST go through <see cref="IDataProtectionProvider"/> with a
    /// concern-specific purpose string so each concern can rotate keys
    /// independently and prevent cross-purpose decryption.
    /// </summary>
    public const string OidcClientSecretProtectorPurpose = "Verbara.OidcClientSecret";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _oidcSecretProtector;

    public PostgresTenantAuthConfigStore(NpgsqlDataSource dataSource, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _dataSource = dataSource;
        _oidcSecretProtector = dataProtectionProvider.CreateProtector(OidcClientSecretProtectorPurpose);
    }

    /// <summary>
    /// Returns the unwrapped (plaintext) secret expected by callers
    /// (notably <c>OidcEndpoints.HandleCallback</c> at line 140 which feeds
    /// it directly into the OIDC token-exchange request body). On
    /// <see cref="CryptographicException"/> — which means the row contains
    /// legacy plaintext that the
    /// <c>OidcClientSecretEncryptionMigrator</c> has not yet rewritten —
    /// the value is returned verbatim. The migrator runs on host startup
    /// and is idempotent; the catch is a transitional belt-and-suspenders
    /// guard so an early-loaded request between deploy and migrator
    /// completion doesn't fail.
    /// </summary>
    internal string? UnprotectSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return _oidcSecretProtector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Legacy plaintext row. Returning the raw value preserves the OIDC
            // token-exchange flow until the migrator catches up. Protect-on-write
            // ensures every NEW save is encrypted from this point forward.
            return value;
        }
    }

    /// <summary>
    /// Writes the encrypted form so plaintext never lands in the row.
    /// </summary>
    internal string? ProtectSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return _oidcSecretProtector.Protect(value);
    }

    public async Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TenantAuthConfigRow>(
            "SELECT tenant_id, mfa_policy, mfa_required_roles, password_min_length, password_require_uppercase, " +
            "password_require_number, password_require_special, lockout_threshold, lockout_duration_minutes, " +
            "session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, oidc_authority, " +
            "oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, " +
            "impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes, ip_allowlist_enabled, updated_at " +
            "FROM tenant_auth_config WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
        return row?.ToTenantAuthConfig(this);
    }

    public async Task SaveAsync(TenantAuthConfig config, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_auth_config (tenant_id, mfa_policy, mfa_required_roles, password_min_length, " +
            "password_require_uppercase, password_require_number, password_require_special, lockout_threshold, " +
            "lockout_duration_minutes, session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, " +
            "oidc_authority, oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, " +
            "impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes, ip_allowlist_enabled, updated_at) " +
            "VALUES (@TenantId, @MfaPolicy, @MfaRequiredRoles, @PasswordMinLength, @PasswordRequireUppercase, " +
            "@PasswordRequireNumber, @PasswordRequireSpecial, @LockoutThreshold, @LockoutDurationMinutes, " +
            "@SessionIdleTimeoutMinutes, @SessionAbsoluteTimeoutHours, @OidcEnabled, @OidcAuthority, " +
            "@OidcClientId, @OidcClientSecret, @OidcAutoCreateUsers, @OidcDefaultRole, " +
            "@ImpersonationMaxConcurrentSessions, @ImpersonationAutoTimeoutMinutes, @IpAllowlistEnabled, @UpdatedAt) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  mfa_policy = EXCLUDED.mfa_policy, mfa_required_roles = EXCLUDED.mfa_required_roles, " +
            "  password_min_length = EXCLUDED.password_min_length, password_require_uppercase = EXCLUDED.password_require_uppercase, " +
            "  password_require_number = EXCLUDED.password_require_number, password_require_special = EXCLUDED.password_require_special, " +
            "  lockout_threshold = EXCLUDED.lockout_threshold, lockout_duration_minutes = EXCLUDED.lockout_duration_minutes, " +
            "  session_idle_timeout_minutes = EXCLUDED.session_idle_timeout_minutes, session_absolute_timeout_hours = EXCLUDED.session_absolute_timeout_hours, " +
            "  oidc_enabled = EXCLUDED.oidc_enabled, oidc_authority = EXCLUDED.oidc_authority, " +
            "  oidc_client_id = EXCLUDED.oidc_client_id, oidc_client_secret = EXCLUDED.oidc_client_secret, " +
            "  oidc_auto_create_users = EXCLUDED.oidc_auto_create_users, oidc_default_role = EXCLUDED.oidc_default_role, " +
            "  impersonation_max_concurrent_sessions = EXCLUDED.impersonation_max_concurrent_sessions, " +
            "  impersonation_auto_timeout_minutes = EXCLUDED.impersonation_auto_timeout_minutes, " +
            "  ip_allowlist_enabled = EXCLUDED.ip_allowlist_enabled, " +
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
                // ADMIN-001 (PREPUB-2026-05-09): wrap on write — never write plaintext.
                OidcClientSecret = ProtectSecret(config.OidcClientSecret),
                config.OidcAutoCreateUsers,
                config.OidcDefaultRole,
                config.ImpersonationMaxConcurrentSessions,
                config.ImpersonationAutoTimeoutMinutes,
                config.IpAllowlistEnabled,
                config.UpdatedAt,
            });
    }

    private sealed class TenantAuthConfigRow
    {
        public string tenant_id { get; init; } = null!;
        public string mfa_policy { get; init; } = null!;
        public string[]? mfa_required_roles { get; init; }
        public int password_min_length { get; init; }
        public bool password_require_uppercase { get; init; }
        public bool password_require_number { get; init; }
        public bool password_require_special { get; init; }
        public int lockout_threshold { get; init; }
        public int lockout_duration_minutes { get; init; }
        public int session_idle_timeout_minutes { get; init; }
        public int session_absolute_timeout_hours { get; init; }
        public bool oidc_enabled { get; init; }
        public string? oidc_authority { get; init; }
        public string? oidc_client_id { get; init; }
        public string? oidc_client_secret { get; init; }
        public bool oidc_auto_create_users { get; init; }
        public string oidc_default_role { get; init; } = null!;
        public int impersonation_max_concurrent_sessions { get; init; } = 3;
        public int impersonation_auto_timeout_minutes { get; init; } = 240;
        public bool ip_allowlist_enabled { get; init; }
        public DateTime? updated_at { get; init; }

        public TenantAuthConfig ToTenantAuthConfig(PostgresTenantAuthConfigStore store) => new()
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
            // ADMIN-001 (PREPUB-2026-05-09): unwrap on read — internal callers
            // (OidcEndpoints token-exchange) receive plaintext; the API layer
            // re-projects to TenantAuthConfigResponse so plaintext never
            // crosses the HTTP boundary.
            OidcClientSecret = store.UnprotectSecret(oidc_client_secret),
            OidcAutoCreateUsers = oidc_auto_create_users,
            OidcDefaultRole = oidc_default_role,
            ImpersonationMaxConcurrentSessions = impersonation_max_concurrent_sessions,
            ImpersonationAutoTimeoutMinutes = impersonation_auto_timeout_minutes,
            IpAllowlistEnabled = ip_allowlist_enabled,
            UpdatedAt = updated_at,
        };
    }
}
