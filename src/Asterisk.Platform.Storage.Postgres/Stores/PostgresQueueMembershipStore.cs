using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresQueueMembershipStore : IQueueMembershipStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresQueueMembershipStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<QueueMembership>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MembershipRow>(
            "SELECT tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at " +
            "FROM queue_memberships WHERE tenant_id = @TenantId ORDER BY queue_id, agent_id",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<QueueMembership>> ListByQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MembershipRow>(
            "SELECT tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at " +
            "FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId ORDER BY penalty, agent_id",
            new { TenantId = tenantId.Value, QueueId = queueId.Value });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<QueueMembership?> GetAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MembershipRow>(
            "SELECT tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at " +
            "FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId AND agent_id = @AgentId",
            new { TenantId = tenantId.Value, QueueId = queueId.Value, AgentId = agentId.Value });
        return row?.ToModel();
    }

    public async Task SaveAsync(QueueMembership membership, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO queue_memberships (tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at) " +
            "VALUES (@TenantId, @QueueId, @AgentId, @Penalty, @Source, @IsExcluded, @CreatedAt) " +
            "ON CONFLICT (tenant_id, queue_id, agent_id) DO UPDATE SET " +
            "  penalty = EXCLUDED.penalty, source = EXCLUDED.source, is_excluded = EXCLUDED.is_excluded",
            new
            {
                TenantId = membership.TenantId.Value,
                QueueId = membership.QueueId.Value,
                AgentId = membership.AgentId.Value,
                membership.Penalty,
                Source = membership.Source.ToString().ToLowerInvariant(),
                membership.IsExcluded,
                membership.CreatedAt,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId AND agent_id = @AgentId",
            new { TenantId = tenantId.Value, QueueId = queueId.Value, AgentId = agentId.Value });
    }

    public async Task DeleteAllForQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            new { TenantId = tenantId.Value, QueueId = queueId.Value });
    }

    public async Task DeleteAllForAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM queue_memberships WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            new { TenantId = tenantId.Value, AgentId = agentId.Value });
    }

    private sealed class MembershipRow
    {
        public string tenant_id { get; init; } = null!;
        public string queue_id { get; init; } = null!;
        public string agent_id { get; init; } = null!;
        public int penalty { get; init; }
        public string source { get; init; } = null!;
        public bool is_excluded { get; init; }
        public DateTime created_at { get; init; }

        public QueueMembership ToModel() => new()
        {
            TenantId = new TenantId(tenant_id),
            QueueId = EntityId.From(queue_id),
            AgentId = EntityId.From(agent_id),
            Penalty = penalty,
            Source = source == "manual" ? MembershipSource.Manual : MembershipSource.Skill,
            IsExcluded = is_excluded,
            CreatedAt = created_at,
        };
    }
}
