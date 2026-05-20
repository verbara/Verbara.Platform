using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAgentStore : IAgentStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAgentStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("AgentId", agentId.Value));
            },
            r => AgentRow.Map(r, includeSip: true), ct);
        return row?.ToAgent();
    }

    public async Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND user_id = @UserId LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            // This SELECT deliberately omits the extension / sip_password columns
            // (matching the original Dapper query); the row must not read them.
            r => AgentRow.Map(r, includeSip: false), ct);
        return row?.ToAgent();
    }

    public async Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND extension = @Extension LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Extension", extension));
            },
            r => AgentRow.Map(r, includeSip: true), ct);
        return row?.ToAgent();
    }

    public async Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct)
    {
        var whereClauses = new List<string> { "tenant_id = @TenantId" };
        var binders = new List<Action<NpgsqlParameterCollection>>
        {
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
        };

        if (query.State.HasValue)
        {
            whereClauses.Add("state = @State");
            binders.Add(p => p.Add(new NpgsqlParameter("State", (int)query.State.Value)));
        }
        if (query.TeamId.HasValue)
        {
            whereClauses.Add("team_id = @TeamId");
            binders.Add(p => p.Add(new NpgsqlParameter("TeamId", query.TeamId.Value.Value)));
        }

        var where = string.Join(" AND ", whereClauses);
        var offset = (query.Page - 1) * query.PageSize;

        void BindFilters(NpgsqlParameterCollection p) { foreach (var b in binders) b(p); }

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            $"SELECT COUNT(*) FROM agents WHERE {where}", BindFilters, ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            $"extension, sip_password, created_at, updated_at, created_by, updated_by " +
            $"FROM agents WHERE {where} ORDER BY display_name LIMIT @Limit OFFSET @Offset",
            p =>
            {
                BindFilters(p);
                p.Add(new NpgsqlParameter("Limit", query.PageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            r => AgentRow.Map(r, includeSip: true), ct);

        var items = rows.Select(r => r.ToAgent()).ToList();
        return new PagedResult<Agent>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Agent agent, CancellationToken ct)
    {
        var capacityJson = JsonSerializer.Serialize(agent.Capacity, PostgresJson.Ctx.ChannelCapacity);
        var skillsJson = JsonSerializer.Serialize(agent.Skills, PostgresJson.Ctx.IReadOnlyListString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO agents (agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@AgentId, @TenantId, @UserId, @DisplayName, @State, @Capacity::jsonb, @TeamId, @Skills::jsonb, " +
            "@Extension, @SipPassword, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, agent_id) DO UPDATE SET " +
            "  display_name = EXCLUDED.display_name, state = EXCLUDED.state, capacity = EXCLUDED.capacity, " +
            "  team_id = EXCLUDED.team_id, skills = EXCLUDED.skills, " +
            "  extension = EXCLUDED.extension, sip_password = EXCLUDED.sip_password, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            p =>
            {
                p.Add(new NpgsqlParameter("AgentId", agent.AgentId.Value));
                p.Add(new NpgsqlParameter("TenantId", agent.TenantId.Value));
                p.Add(new NpgsqlParameter("UserId", agent.UserId.Value));
                p.Add(new NpgsqlParameter("DisplayName", agent.DisplayName));
                p.Add(new NpgsqlParameter("State", (int)agent.State));
                p.Add(new NpgsqlParameter("Capacity", capacityJson));
                p.Add(new NpgsqlParameter("TeamId", NpgsqlDbType.Text) { Value = (object?)agent.TeamId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Skills", skillsJson));
                p.Add(new NpgsqlParameter("Extension", NpgsqlDbType.Varchar) { Value = (object?)agent.Extension ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SipPassword", NpgsqlDbType.Varchar) { Value = (object?)agent.SipPassword ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", agent.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)agent.UpdatedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)agent.CreatedBy ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)agent.UpdatedBy ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("AgentId", agentId.Value));
            },
            ct);
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

        // GetByUserIdAsync's SELECT omits the extension / sip_password columns
        // (preserved from the original Dapper query, where missing columns simply
        // stayed null). includeSip gates those two reads so we don't call
        // GetOrdinal on a column the reader doesn't expose.
        public static AgentRow Map(NpgsqlDataReader r, bool includeSip) => new()
        {
            agent_id = r.GetString("agent_id"),
            tenant_id = r.GetString("tenant_id"),
            user_id = r.GetString("user_id"),
            display_name = r.GetString("display_name"),
            state = r.GetInt32("state"),
            capacity = r.GetString("capacity"),
            team_id = r.GetStringOrNull("team_id"),
            skills = r.GetString("skills"),
            extension = includeSip ? r.GetStringOrNull("extension") : null,
            sip_password = includeSip ? r.GetStringOrNull("sip_password") : null,
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };

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
