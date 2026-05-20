using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresQueueMembershipStore : IQueueMembershipStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresQueueMembershipStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<QueueMembership>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at " +
            "FROM queue_memberships WHERE tenant_id = @TenantId ORDER BY queue_id, agent_id",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            MembershipRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<QueueMembership>> ListByQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at " +
            "FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId ORDER BY penalty, agent_id",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); },
            MembershipRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<QueueMembership?> GetAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at " +
            "FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId AND agent_id = @AgentId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); p.Add(new NpgsqlParameter("AgentId", agentId.Value)); },
            MembershipRow.Map, ct);
        return row?.ToModel();
    }

    public async Task SaveAsync(QueueMembership membership, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO queue_memberships (tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at) " +
            "VALUES (@TenantId, @QueueId, @AgentId, @Penalty, @Source, @IsExcluded, @CreatedAt) " +
            "ON CONFLICT (tenant_id, queue_id, agent_id) DO UPDATE SET " +
            "  penalty = EXCLUDED.penalty, source = EXCLUDED.source, is_excluded = EXCLUDED.is_excluded",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", membership.TenantId.Value));
                p.Add(new NpgsqlParameter("QueueId", membership.QueueId.Value));
                p.Add(new NpgsqlParameter("AgentId", membership.AgentId.Value));
                p.Add(new NpgsqlParameter("Penalty", membership.Penalty));
                p.Add(new NpgsqlParameter("Source", membership.Source.ToString().ToLowerInvariant()));
                p.Add(new NpgsqlParameter("IsExcluded", membership.IsExcluded));
                p.Add(new NpgsqlParameter("CreatedAt", membership.CreatedAt));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId queueId, EntityId agentId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId AND agent_id = @AgentId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); p.Add(new NpgsqlParameter("AgentId", agentId.Value)); },
            ct);
    }

    public async Task DeleteAllForQueueAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM queue_memberships WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); },
            ct);
    }

    public async Task DeleteAllForAgentAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM queue_memberships WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("AgentId", agentId.Value)); },
            ct);
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

        public static MembershipRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            queue_id = r.GetString("queue_id"),
            agent_id = r.GetString("agent_id"),
            penalty = r.GetInt32("penalty"),
            source = r.GetString("source"),
            is_excluded = r.GetBoolean("is_excluded"),
            created_at = r.GetDateTime("created_at"),
        };

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
