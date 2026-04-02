using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresFlowStore : IFlowStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFlowStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<FlowDefinition>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FlowRow>(
            "SELECT DISTINCT ON (flow_id) flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at " +
            "FROM flow_definitions WHERE tenant_id = @TenantId " +
            "ORDER BY flow_id, version DESC",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToFlowDefinition()).ToList();
    }

    public async Task<FlowDefinition?> GetByIdAsync(TenantId tenantId, EntityId flowId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<FlowRow>(
            "SELECT flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at " +
            "FROM flow_definitions WHERE tenant_id = @TenantId AND flow_id = @FlowId " +
            "ORDER BY version DESC LIMIT 1",
            new { TenantId = tenantId.Value, FlowId = flowId.Value });
        return row?.ToFlowDefinition();
    }

    public async Task<FlowDefinition?> GetPublishedAsync(TenantId tenantId, EntityId flowId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<FlowRow>(
            "SELECT flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at " +
            "FROM flow_definitions WHERE tenant_id = @TenantId AND flow_id = @FlowId AND is_published = true " +
            "ORDER BY version DESC LIMIT 1",
            new { TenantId = tenantId.Value, FlowId = flowId.Value });
        return row?.ToFlowDefinition();
    }

    public async Task SaveAsync(FlowDefinition flow, CancellationToken ct)
    {
        var nodesJson = JsonSerializer.Serialize(flow.Nodes, PostgresJson.Ctx.IReadOnlyListFlowNode);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO flow_definitions (flow_id, tenant_id, name, version, is_published, entry_node_id, nodes, created_at) " +
            "VALUES (@FlowId, @TenantId, @Name, @Version, @IsPublished, @EntryNodeId, @Nodes::jsonb, @CreatedAt) " +
            "ON CONFLICT (tenant_id, flow_id, version) DO UPDATE SET " +
            "  name = EXCLUDED.name, is_published = EXCLUDED.is_published, " +
            "  entry_node_id = EXCLUDED.entry_node_id, nodes = EXCLUDED.nodes",
            new
            {
                FlowId = flow.FlowId.Value,
                TenantId = flow.TenantId.Value,
                flow.Name,
                flow.Version,
                flow.IsPublished,
                EntryNodeId = flow.EntryNodeId.Value,
                Nodes = nodesJson,
                flow.CreatedAt,
            });
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
