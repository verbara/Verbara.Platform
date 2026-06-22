using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed per-tenant BYO LLM config store (P2c.1). The API key column is
/// <see cref="IDataProtector"/>-protected at rest (purpose <see cref="ProtectorPurpose"/>),
/// mirroring <see cref="PostgresTenantAuthConfigStore"/>'s OIDC-secret column. The
/// type-specific <c>provider_settings</c> is a JSONB column (de)serialized reflection-free.
/// </summary>
internal sealed class PostgresTenantLlmConfigStore : ITenantLlmConfigStore
{
    /// <summary>
    /// DataProtection purpose for the per-tenant LLM API key column. Concern-specific so this
    /// secret rotates independently of every other column-level secret (ADR-0003 extension).
    /// </summary>
    public const string ProtectorPurpose = "Verbara.Platform.Typification.TenantLlmApiKey.v1";

    private const string SelectColumns =
        "tenant_id, provider_type, model, api_key_encrypted, api_key_last4, " +
        "provider_settings, enabled, created_at, updated_at";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;

    public PostgresTenantLlmConfigStore(NpgsqlDataSource dataSource, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _dataSource = dataSource;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>
    /// Unwraps the stored API key to plaintext for server-side use (the resolver materialises it
    /// at provider-build time; it never leaves the server). On <see cref="CryptographicException"/>
    /// — a legacy plaintext row a future migrator hasn't yet rewritten — the value is returned
    /// verbatim so an early request between deploy and migrator completion doesn't fail.
    /// </summary>
    internal string? UnprotectKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return value;
        }
    }

    /// <summary>Wraps the API key so plaintext never lands in the row.</summary>
    internal string? ProtectKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return _protector.Protect(value);
    }

    public async Task<TenantLlmConfig?> GetAsync(EntityId tenantId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM tenant_llm_config WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            Row.Map, ct);
        return row?.ToConfig(this);
    }

    public async Task UpsertAsync(TenantLlmConfig config, CancellationToken ct)
    {
        var settingsJson = JsonSerializer.Serialize(config.Settings, PostgresJson.Ctx.ProviderSettings);

        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_llm_config " +
            "(tenant_id, provider_type, model, api_key_encrypted, api_key_last4, " +
            " provider_settings, enabled, created_at, updated_at) " +
            "VALUES (@TenantId, @ProviderType, @Model, @ApiKeyEncrypted, @ApiKeyLast4, " +
            " @ProviderSettings::jsonb, @Enabled, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  provider_type = EXCLUDED.provider_type, " +
            "  model = EXCLUDED.model, " +
            "  api_key_encrypted = EXCLUDED.api_key_encrypted, " +
            "  api_key_last4 = EXCLUDED.api_key_last4, " +
            "  provider_settings = EXCLUDED.provider_settings, " +
            "  enabled = EXCLUDED.enabled, " +
            "  updated_at = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", config.TenantId.Value));
                p.Add(new NpgsqlParameter("ProviderType", config.ProviderType.ToString()));
                p.Add(new NpgsqlParameter("Model", config.Model));
                p.Add(new NpgsqlParameter("ApiKeyEncrypted", NpgsqlDbType.Text)
                    { Value = (object?)ProtectKey(config.ApiKey) ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ApiKeyLast4", NpgsqlDbType.Text)
                    { Value = (object?)config.ApiKeyLast4 ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ProviderSettings", settingsJson));
                p.Add(new NpgsqlParameter("Enabled", config.Enabled));
                p.Add(new NpgsqlParameter("CreatedAt", config.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", config.UpdatedAt.UtcDateTime));
            },
            ct);
    }

    public async Task DeleteAsync(EntityId tenantId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM tenant_llm_config WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            ct);
    }

    private sealed class Row
    {
        public string tenant_id { get; init; } = null!;
        public string provider_type { get; init; } = null!;
        public string model { get; init; } = null!;
        public string? api_key_encrypted { get; init; }
        public string? api_key_last4 { get; init; }
        public string provider_settings { get; init; } = null!;
        public bool enabled { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }

        public static Row Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            provider_type = r.GetString("provider_type"),
            model = r.GetString("model"),
            api_key_encrypted = r.GetStringOrNull("api_key_encrypted"),
            api_key_last4 = r.GetStringOrNull("api_key_last4"),
            provider_settings = r.GetString("provider_settings"),
            enabled = r.GetBoolean("enabled"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTime("updated_at"),
        };

        public TenantLlmConfig ToConfig(PostgresTenantLlmConfigStore store)
        {
            var settings = JsonSerializer.Deserialize(provider_settings, PostgresJson.Ctx.ProviderSettings)
                ?? new ProviderSettings();

            return new TenantLlmConfig
            {
                TenantId = EntityId.From(tenant_id),
                ProviderType = Enum.TryParse<ProviderType>(provider_type, out var pt) ? pt : ProviderType.OpenAiCompatible,
                Model = model,
                // Unwrap on read — server-only; the API layer masks it to keySet + last4 so plaintext
                // never crosses the HTTP boundary.
                ApiKey = store.UnprotectKey(api_key_encrypted),
                ApiKeyLast4 = api_key_last4,
                Settings = settings,
                Enabled = enabled,
                CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(updated_at, TimeSpan.Zero),
            };
        }
    }
}
