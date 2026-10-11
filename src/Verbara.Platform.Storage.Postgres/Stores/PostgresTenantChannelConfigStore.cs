using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres store of each tenant's channel configuration. Secret credential values — the canonical secret
/// keys of a channel's declared schema, or every key of a channel without one
/// (<see cref="ChannelCredentialSchemas.IsSecret"/>) — are wrapped with DataProtection under
/// <see cref="CredentialsProtectorPurpose"/> before they reach the <c>credentials</c> jsonb column and
/// unwrapped on read (whatsapp-works-for-real D15; precedent: <see cref="PostgresUserStore"/>'s MFA columns).
/// A secret value that does not unwrap is a legacy plaintext value the
/// <see cref="TenantChannelCredentialsEncryptionMigrator"/> has not reached yet, and is returned verbatim.
/// Non-secret keys and the column's schema are unchanged.
/// </summary>
internal sealed class PostgresTenantChannelConfigStore : ITenantChannelConfigStore
{
    /// <summary>
    /// DataProtection purpose for secret channel credential values. A persistence contract: changing it makes
    /// every wrapped value unreadable. The migrator binds the same constant.
    /// </summary>
    public const string CredentialsProtectorPurpose = "Verbara.Platform.TenantChannelConfig.Credentials.v1";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;

    public PostgresTenantChannelConfigStore(NpgsqlDataSource dataSource, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _dataSource = dataSource;
        _protector = dataProtectionProvider.CreateProtector(CredentialsProtectorPurpose);
    }

    public async Task<TenantChannelConfig?> GetAsync(TenantId tenantId, ChannelType channel, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT tenant_id, channel, credentials, is_active " +
            "FROM tenant_channel_configs WHERE tenant_id = @TenantId AND channel = @Channel",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Integer) { Value = (int)channel });
            },
            ChannelConfigRow.Map, ct);
        return row is null ? null : ToConfig(row);
    }

    public async Task SaveAsync(TenantChannelConfig config, CancellationToken ct)
    {
        var stored = new Dictionary<string, string>(config.Credentials.Count, StringComparer.Ordinal);
        foreach (var (key, value) in config.Credentials)
        {
            stored[key] = ChannelCredentialSchemas.IsSecret(config.Channel, key) && !string.IsNullOrEmpty(value)
                ? _protector.Protect(value)
                : value;
        }

        var credJson = JsonSerializer.Serialize(stored, PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_channel_configs (tenant_id, channel, credentials, is_active) " +
            "VALUES (@TenantId, @Channel, @Credentials::jsonb, @IsActive) " +
            "ON CONFLICT (tenant_id, channel) DO UPDATE SET " +
            "  credentials = EXCLUDED.credentials, is_active = EXCLUDED.is_active",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = config.TenantId.Value });
                p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Integer) { Value = (int)config.Channel });
                p.Add(new NpgsqlParameter("Credentials", NpgsqlDbType.Text) { Value = credJson });
                p.Add(new NpgsqlParameter("IsActive", NpgsqlDbType.Boolean) { Value = config.IsActive });
            },
            ct);
    }

    private TenantChannelConfig ToConfig(ChannelConfigRow row)
    {
        var channel = (ChannelType)row.channel;
        var stored = JsonSerializer.Deserialize(row.credentials, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
                     ?? new Dictionary<string, string>();

        var credentials = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        foreach (var (key, value) in stored)
        {
            credentials[key] = ChannelCredentialSchemas.IsSecret(channel, key) && !string.IsNullOrEmpty(value)
                ? UnprotectOrVerbatim(value)
                : value;
        }

        return new TenantChannelConfig
        {
            TenantId = new TenantId(row.tenant_id),
            Channel = channel,
            Credentials = credentials,
            IsActive = row.is_active,
        };
    }

    private string UnprotectOrVerbatim(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Legacy plaintext the startup migrator has not wrapped yet: protect-on-write plus the migrator
            // converge the column, and until then the value is usable as stored.
            return value;
        }
    }

    private sealed class ChannelConfigRow
    {
        public string tenant_id { get; init; } = null!;
        public int channel { get; init; }
        public string credentials { get; init; } = null!;
        public bool is_active { get; init; }

        public static ChannelConfigRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            channel = r.GetInt32("channel"),
            credentials = r.GetString("credentials"),
            is_active = r.GetBoolean("is_active"),
        };
    }
}
