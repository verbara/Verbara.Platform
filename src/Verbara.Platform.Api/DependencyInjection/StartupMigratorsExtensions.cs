using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Storage.Postgres;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// Registers the one-shot, idempotent startup migrators that backfill column-level
/// secrets and seed rows the schema runner cannot express. Grouped into one registrar
/// so the composition root carries a single call and the set is unit-testable without
/// a host — the same shape as <see cref="RealtimeSyncingStoresExtensions"/> and
/// <c>AuthHotpathCachingExtensions</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every migrator here is a Postgres-backed <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// whose constructor requires the <c>NpgsqlDataSource</c> that only <c>AddPostgresStorage</c>
/// registers, so the set is registered ONLY when a core Postgres connection string is
/// configured. The predicate lives here rather than at the call site so the "no Postgres,
/// no migrators" rule is expressed once and covered by tests.
/// </para>
/// <para>
/// <b>Order is part of the contract.</b> <c>IHostedService</c> instances start in
/// registration order, so the three are registered in the order below and pinned by
/// <c>StartupMigratorsRegistrarTests</c>.
/// </para>
/// </remarks>
public static class StartupMigratorsExtensions
{
    /// <summary>
    /// Registers the Postgres startup migrators when <paramref name="coreConnectionString"/>
    /// is configured; a no-op otherwise (the InMemory storage path has nothing to migrate).
    /// Call AFTER <c>AddPostgresStorage</c> and after the schema runner
    /// (<c>DatabaseMigrationService.ApplyMigrations</c>) — the migrators read tables the
    /// schema runner creates.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="coreConnectionString">
    /// The core Postgres connection string, already normalized by
    /// <c>ConnectionStringDefaults.ApplyPoolDefaults</c>. Null or empty means this deployment
    /// runs on InMemory storage, so nothing is registered.
    /// </param>
    /// <remarks>
    /// The three migrators, in start order:
    /// <list type="number">
    /// <item><description>
    /// <b>OIDC client secret</b> (ADMIN-001, PREPUB-2026-05-09) — encrypts any legacy plaintext
    /// <c>oidc_client_secret</c> row in <c>tenant_auth_config</c>. Re-runs are no-ops once every
    /// row is encrypted.
    /// </description></item>
    /// <item><description>
    /// <b>User MFA material</b> (threat-model asset A7) — wraps any legacy unwrapped
    /// <c>users.mfa_secret</c> / <c>users.mfa_recovery_codes</c> value. Never blocks startup;
    /// re-runs are zero-write no-ops once every value is wrapped.
    /// </description></item>
    /// <item><description>
    /// <b>Tenant LLM config seed</b> (P2c.1 §6) — materialises the appsettings global LLM key
    /// into the single operational tenant's <c>tenant_llm_config</c> row. No-op unless the global
    /// key is set AND exactly one operational tenant exists AND it has no config row yet.
    /// </description></item>
    /// </list>
    /// All three are idempotent and safe to leave registered indefinitely.
    /// </remarks>
    public static IServiceCollection AddPlatformStartupMigrators(
        this IServiceCollection services,
        string? coreConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrEmpty(coreConnectionString))
        {
            return services;
        }

        services.AddOidcClientSecretEncryptionMigrator();
        services.AddUserMfaEncryptionMigrator();
        services.AddTenantLlmConfigSeedMigrator();
        return services;
    }
}
