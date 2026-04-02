using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantChannelConfigStore : ITenantChannelConfigStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantChannelConfigStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantChannelConfig?> GetAsync(TenantId tenantId, ChannelType channel, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ChannelConfigRow>(
            "SELECT tenant_id, channel, credentials, is_active " +
            "FROM tenant_channel_configs WHERE tenant_id = @TenantId AND channel = @Channel",
            new { TenantId = tenantId.Value, Channel = (int)channel });
        return row?.ToConfig();
    }

    public async Task SaveAsync(TenantChannelConfig config, CancellationToken ct)
    {
        var credJson = JsonSerializer.Serialize(config.Credentials, PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_channel_configs (tenant_id, channel, credentials, is_active) " +
            "VALUES (@TenantId, @Channel, @Credentials::jsonb, @IsActive) " +
            "ON CONFLICT (tenant_id, channel) DO UPDATE SET " +
            "  credentials = EXCLUDED.credentials, is_active = EXCLUDED.is_active",
            new
            {
                TenantId = config.TenantId.Value,
                Channel = (int)config.Channel,
                Credentials = credJson,
                config.IsActive,
            });
    }

    private sealed class ChannelConfigRow
    {
        public string tenant_id { get; init; } = null!;
        public int channel { get; init; }
        public string credentials { get; init; } = null!;
        public bool is_active { get; init; }

        public TenantChannelConfig ToConfig()
        {
            var creds = JsonSerializer.Deserialize(credentials, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
                        ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
            return new TenantChannelConfig
            {
                TenantId = new TenantId(tenant_id),
                Channel = (ChannelType)channel,
                Credentials = creds,
                IsActive = is_active,
            };
        }
    }
}
