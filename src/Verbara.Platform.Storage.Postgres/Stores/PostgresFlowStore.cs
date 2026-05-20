using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresFlowStore : IFlowStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFlowStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<FlowDefinition>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT DISTINCT ON (flow_id) flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at " +
            "FROM flow_definitions WHERE tenant_id = @TenantId " +
            "ORDER BY flow_id, version DESC",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            FlowRow.Map, ct);
        return rows.Select(r => r.ToFlowDefinition()).ToList();
    }

    public async Task<FlowDefinition?> GetByIdAsync(TenantId tenantId, EntityId flowId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at " +
            "FROM flow_definitions WHERE tenant_id = @TenantId AND flow_id = @FlowId " +
            "ORDER BY version DESC LIMIT 1",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("FlowId", flowId.Value)); },
            FlowRow.Map, ct);
        return row?.ToFlowDefinition();
    }

    public async Task<FlowDefinition?> GetPublishedAsync(TenantId tenantId, EntityId flowId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at " +
            "FROM flow_definitions WHERE tenant_id = @TenantId AND flow_id = @FlowId AND is_published = true " +
            "ORDER BY version DESC LIMIT 1",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("FlowId", flowId.Value)); },
            FlowRow.Map, ct);
        return row?.ToFlowDefinition();
    }

    public async Task SaveAsync(FlowDefinition flow, CancellationToken ct)
    {
        var nodesJson = JsonSerializer.Serialize(flow.Nodes, PostgresJson.Ctx.IReadOnlyListFlowNode);

        await _dataSource.ExecuteAsync(
            "INSERT INTO flow_definitions (flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at) " +
            "VALUES (@FlowId, @TenantId, @Name, @Version, @IsPublished, @EntryNodeId, @Nodes::jsonb, @CreatedAt) " +
            "ON CONFLICT (tenant_id, flow_id, version) DO UPDATE SET " +
            "  name = EXCLUDED.name, is_published = EXCLUDED.is_published, " +
            "  entry_node_id = EXCLUDED.entry_node_id, nodes = EXCLUDED.nodes",
            p =>
            {
                p.Add(new NpgsqlParameter("FlowId",      flow.FlowId.Value));
                p.Add(new NpgsqlParameter("TenantId",    flow.TenantId.Value));
                p.Add(new NpgsqlParameter("Name",        flow.Name));
                p.Add(new NpgsqlParameter("Version",     flow.Version));
                p.Add(new NpgsqlParameter("IsPublished", flow.IsPublished));
                p.Add(new NpgsqlParameter("EntryNodeId", flow.EntryNodeId.Value));
                p.Add(new NpgsqlParameter("Nodes",       nodesJson));
                p.Add(new NpgsqlParameter("CreatedAt",   flow.CreatedAt));
            },
            ct);
    }

    private sealed class FlowRow
    {
        public string flow_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public int version { get; init; }
        public bool is_published { get; init; }
        public string entry_node_id { get; init; } = null!;
        public string nodes { get; init; } = null!;
        public DateTime created_at { get; init; }

        public static FlowRow Map(NpgsqlDataReader r) => new()
        {
            flow_id       = r.GetString("flow_id"),
            tenant_id     = r.GetString("tenant_id"),
            name          = r.GetString("name"),
            version       = r.GetInt32("version"),
            is_published  = r.GetBoolean("is_published"),
            entry_node_id = r.GetString("entry_node_id"),
            nodes         = r.GetString("nodes"),
            created_at    = r.GetDateTime("created_at"),
        };

        public FlowDefinition ToFlowDefinition()
        {
            var nodeList = JsonSerializer.Deserialize(nodes, PostgresJson.Ctx.IReadOnlyListFlowNode)
                           ?? (IReadOnlyList<FlowNode>)[];
            return new FlowDefinition
            {
                FlowId = EntityId.From(flow_id),
                TenantId = new TenantId(tenant_id),
                Name = name,
                Version = version,
                IsPublished = is_published,
                EntryNodeId = EntityId.From(entry_node_id),
                Nodes = nodeList,
                CreatedAt = created_at,
            };
        }
    }
}
