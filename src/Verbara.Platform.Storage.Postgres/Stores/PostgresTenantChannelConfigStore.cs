using System.Text.Json;
using Npgsql;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantChannelConfigStore : ITenantChannelConfigStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantChannelConfigStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantChannelConfig?> GetAsync(TenantId tenantId, ChannelType channel, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT tenant_id, channel, credentials, is_active " +
            "FROM tenant_channel_configs WHERE tenant_id = @TenantId AND channel = @Channel",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Channel", (int)channel));
            },
            ChannelConfigRow.Map, ct);
        return row?.ToConfig();
    }

    public async Task SaveAsync(TenantChannelConfig config, CancellationToken ct)
    {
        var credJson = JsonSerializer.Serialize(config.Credentials, PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_channel_configs (tenant_id, channel, credentials, is_active) " +
            "VALUES (@TenantId, @Channel, @Credentials::jsonb, @IsActive) " +
            "ON CONFLICT (tenant_id, channel) DO UPDATE SET " +
            "  credentials = EXCLUDED.credentials, is_active = EXCLUDED.is_active",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", config.TenantId.Value));
                p.Add(new NpgsqlParameter("Channel", (int)config.Channel));
                p.Add(new NpgsqlParameter("Credentials", credJson));
                p.Add(new NpgsqlParameter("IsActive", config.IsActive));
            },
            ct);
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
