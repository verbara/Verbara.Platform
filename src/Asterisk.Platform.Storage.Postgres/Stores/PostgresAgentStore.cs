using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAgentStore : IAgentStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAgentStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AgentRow>(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            new { TenantId = tenantId.Value, AgentId = agentId.Value });
        return row?.ToAgent();
    }

    public async Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AgentRow>(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND user_id = @UserId LIMIT 1",
            new { TenantId = tenantId.Value, UserId = userId.Value });
        return row?.ToAgent();
    }

    public async Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AgentRow>(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND extension = @Extension LIMIT 1",
            new { TenantId = tenantId.Value, Extension = extension });
        return row?.ToAgent();
    }

    public async Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var whereClauses = new List<string> { "tenant_id = @TenantId" };
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId.Value);

        if (query.State.HasValue)
        {
            whereClauses.Add("state = @State");
            parameters.Add("State", (int)query.State.Value);
        }
        if (query.TeamId.HasValue)
        {
            whereClauses.Add("team_id = @TeamId");
            parameters.Add("TeamId", query.TeamId.Value.Value);
        }

        var where = string.Join(" AND ", whereClauses);
        var offset = (query.Page - 1) * query.PageSize;

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM agents WHERE {where}", parameters);

        parameters.Add("Limit", query.PageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<AgentRow>(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            $"extension, sip_password, created_at, updated_at, created_by, updated_by " +
            $"FROM agents WHERE {where} ORDER BY display_name LIMIT @Limit OFFSET @Offset",
            parameters);

        var items = rows.Select(r => r.ToAgent()).ToList();
        return new PagedResult<Agent>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Agent agent, CancellationToken ct)
    {
        var capacityJson = JsonSerializer.Serialize(agent.Capacity, PostgresJson.Ctx.ChannelCapacity);
        var skillsJson = JsonSerializer.Serialize(agent.Skills, PostgresJson.Ctx.IReadOnlyListString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO agents (agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@AgentId, @TenantId, @UserId, @DisplayName, @State, @Capacity::jsonb, @TeamId, @Skills::jsonb, " +
            "@Extension, @SipPassword, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, agent_id) DO UPDATE SET " +
            "  display_name = EXCLUDED.display_name, state = EXCLUDED.state, capacity = EXCLUDED.capacity, " +
            "  team_id = EXCLUDED.team_id, skills = EXCLUDED.skills, " +
            "  extension = EXCLUDED.extension, sip_password = EXCLUDED.sip_password, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                AgentId = agent.AgentId.Value,
                TenantId = agent.TenantId.Value,
                UserId = agent.UserId.Value,
                agent.DisplayName,
                State = (int)agent.State,
                Capacity = capacityJson,
                TeamId = agent.TeamId?.Value,
                Skills = skillsJson,
                agent.Extension,
                agent.SipPassword,
                agent.CreatedAt,
                agent.UpdatedAt,
                agent.CreatedBy,
                agent.UpdatedBy,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            new { TenantId = tenantId.Value, AgentId = agentId.Value });
    }

    private sealed class AgentRow
    {
        public string agent_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string user_id { get; init; } = null!;
        public string display_name { get; init; } = null!;
        public int state { get; init; }
        public string capacity { get; init; } = null!;
        public string? team_id { get; init; }
        public string skills { get; init; } = null!;
        public string? extension { get; init; }
        public string? sip_password { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public Agent ToAgent() => new()
        {
            AgentId = EntityId.From(agent_id),
            TenantId = new TenantId(tenant_id),
            UserId = EntityId.From(user_id),
            DisplayName = display_name,
            State = (AgentState)state,
            Capacity = JsonSerializer.Deserialize(capacity, PostgresJson.Ctx.ChannelCapacity) ?? new ChannelCapacity(),
            TeamId = team_id != null ? EntityId.From(team_id) : null,
            Skills = JsonSerializer.Deserialize(skills, PostgresJson.Ctx.IReadOnlyListString) ?? (IReadOnlyList<string>)[],
            Extension = extension,
            SipPassword = sip_password,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
            CreatedBy = created_by,
            UpdatedBy = updated_by,
        };
    }
}
