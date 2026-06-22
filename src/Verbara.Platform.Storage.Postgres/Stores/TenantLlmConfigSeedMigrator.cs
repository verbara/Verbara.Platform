using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Llm;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// One-shot, idempotent startup seed (P2c.1, spec §6) that materialises the appsettings global
/// <see cref="LlmProviderOptions"/> into the SINGLE operational tenant's <c>tenant_llm_config</c>
/// row — the single-tenant / dev migration path off the retired shared global LLM key.
/// </summary>
/// <remarks>
/// <para>
/// Runs the seed ONLY when ALL of the following hold (else it is a no-op):
/// </para>
/// <list type="number">
///   <item><description><see cref="LlmProviderOptions.IsConfigured"/> — a global key/base-url/model is set;</description></item>
///   <item><description>EXACTLY ONE operational (<see cref="TenantType.Customer"/>) tenant exists
///   (a multi-tenant install has no single "the" tenant to seed — it must BYO);</description></item>
///   <item><description>that tenant has no <c>tenant_llm_config</c> row yet (never clobber an
///   admin-configured provider).</description></item>
/// </list>
/// <para>
/// On seed it INSERTs a row: <c>provider_type = openai_compatible</c>, model + base url from the
/// options, the key <see cref="IDataProtector"/>-protected under the SAME purpose the Postgres
/// store uses (<c>Verbara.Platform.Typification.TenantLlmApiKey.v1</c>), <c>enabled = true</c>.
/// </para>
/// <para>
/// Idempotent: a second run finds the row already present (condition 3 fails) and writes nothing.
/// Missing-table (<c>42P01</c>, fresh install before migrations) is a no-op. The migrator never
/// logs the key value, only counts/flags + an Activity for telemetry.
/// </para>
/// </remarks>
internal sealed partial class TenantLlmConfigSeedMigrator : IHostedService
{
    private static readonly ActivitySource Source = new("Verbara.Platform.TenantLlmConfigSeed");

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;
    private readonly LlmProviderOptions _options;
    private readonly ILogger<TenantLlmConfigSeedMigrator> _logger;

    public TenantLlmConfigSeedMigrator(
        NpgsqlDataSource dataSource,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<LlmProviderOptions> options,
        ILogger<TenantLlmConfigSeedMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _protector = dataProtectionProvider.CreateProtector(
            PostgresTenantLlmConfigStore.ProtectorPurpose);
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity("tenant_llm_config.seed");

        // Condition (a): a global key must be configured. Nothing to seed otherwise.
        if (!_options.IsConfigured)
        {
            LogSkippedUnconfigured(_logger);
            return;
        }

        try
        {
            // Condition (b): exactly ONE operational (Customer) tenant. The operational-tenant
            // definition is TenantType.Customer (mirrors RequireOperationalTenant + PostgresTenantStore).
            var operationalTenants = await _dataSource.QueryListAsync(
                "SELECT tenant_id FROM tenants WHERE type = @Type",
                p => p.Add(new NpgsqlParameter("Type", NpgsqlDbType.Integer)
                    { Value = (int)TenantType.Customer }),
                static r => r.GetString("tenant_id"),
                cancellationToken);

            if (operationalTenants.Count != 1)
            {
                LogSkippedTenantCount(_logger, operationalTenants.Count);
                return;
            }

            var tenantId = operationalTenants[0];

            // Condition (c): no existing config row (never clobber an admin-configured provider).
            var existing = await _dataSource.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM tenant_llm_config WHERE tenant_id = @TenantId",
                p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
                cancellationToken) ?? 0L;

            if (existing > 0)
            {
                LogSkippedRowExists(_logger, tenantId);
                return;
            }

            var now = DateTime.UtcNow;
            var encryptedKey = _protector.Protect(_options.ApiKey!);
            var last4 = _options.ApiKey!.Length >= 4 ? _options.ApiKey[^4..] : _options.ApiKey;
            // Mirror the store's jsonb shape: serialize a ProviderSettings (only BaseUrl is relevant
            // for openai_compatible) through the same source-gen context the store reads with.
            var settingsJson = System.Text.Json.JsonSerializer.Serialize(
                new ProviderSettings { BaseUrl = _options.BaseUrl },
                PostgresJson.Ctx.ProviderSettings);

            await _dataSource.ExecuteAsync(
                "INSERT INTO tenant_llm_config " +
                "(tenant_id, provider_type, model, api_key_encrypted, api_key_last4, " +
                " provider_settings, enabled, created_at, updated_at) " +
                "VALUES (@TenantId, @ProviderType, @Model, @ApiKeyEncrypted, @ApiKeyLast4, " +
                " @ProviderSettings::jsonb, true, @CreatedAt, @UpdatedAt) " +
                "ON CONFLICT (tenant_id) DO NOTHING",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("ProviderType", ProviderType.OpenAiCompatible.ToString()));
                    p.Add(new NpgsqlParameter("Model", _options.Model!));
                    p.Add(new NpgsqlParameter("ApiKeyEncrypted", encryptedKey));
                    p.Add(new NpgsqlParameter("ApiKeyLast4", last4));
                    p.Add(new NpgsqlParameter("ProviderSettings", settingsJson));
                    p.Add(new NpgsqlParameter("CreatedAt", now));
                    p.Add(new NpgsqlParameter("UpdatedAt", now));
                },
                cancellationToken);

            activity?.SetTag("seeded_tenant", tenantId);
            LogSeeded(_logger, tenantId);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // 42P01 = undefined_table. tenants / tenant_llm_config absent on a fresh install where
            // DatabaseMigrationService has not yet applied the schema; treat as a no-op.
            LogTableMissing(_logger);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down before we finished — non-fatal.
        }
        catch (Exception ex)
        {
            // Seed failure must NOT block host startup; the next deploy retries. Log loud, move on.
            LogSeedFailed(_logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information,
        Message = "Seeded per-tenant LLM config from appsettings for the single operational tenant {TenantId}")]
    private static partial void LogSeeded(ILogger logger, string tenantId);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Debug,
        Message = "Per-tenant LLM config seed skipped: no global LLM key configured (LlmProviderOptions not configured)")]
    private static partial void LogSkippedUnconfigured(ILogger logger);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Debug,
        Message = "Per-tenant LLM config seed skipped: expected exactly 1 operational tenant, found {Count}")]
    private static partial void LogSkippedTenantCount(ILogger logger, int count);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Debug,
        Message = "Per-tenant LLM config seed skipped: tenant {TenantId} already has a config row")]
    private static partial void LogSkippedRowExists(ILogger logger, string tenantId);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Debug,
        Message = "tenant_llm_config / tenants not present yet — skipping per-tenant LLM config seed")]
    private static partial void LogTableMissing(ILogger logger);

    [LoggerMessage(EventId = 4106, Level = LogLevel.Error,
        Message = "Per-tenant LLM config seed failed; will retry on next deploy")]
    private static partial void LogSeedFailed(ILogger logger, Exception ex);
}
