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
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("AgentId", agentId.Value));
            },
            AgentRow.Map, ct);
        return row?.ToAgent();
    }

    public async Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        // The ONLY callers are the self-scoped GET/PUT /agents/me (the caller's own
        // record), and GET /agents/me MUST surface extension + sip_password so the
        // in-browser softphone can REGISTER (3A). This SELECT therefore INCLUDES
        // them — omitting them (the pre-3A projection) left the Postgres softphone
        // path returning a null sipPassword while the InMemory test path masked it
        // (3B.2b fix). Agent.SipPassword stays [JsonIgnore], so a raw-entity return
        // elsewhere still can't leak it.
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND user_id = @UserId LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            AgentRow.Map, ct);
        return row?.ToAgent();
    }

    public async Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND extension = @Extension LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Extension", extension));
            },
            AgentRow.Map, ct);
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
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            $"FROM agents WHERE {where} ORDER BY display_name LIMIT @Limit OFFSET @Offset",
            p =>
            {
                BindFilters(p);
                p.Add(new NpgsqlParameter("Limit", query.PageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            AgentRow.Map, ct);

        var items = rows.Select(r => r.ToAgent()).ToList();
        return new PagedResult<Agent>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Agent agent, CancellationToken ct)
    {
        var capacityJson = JsonSerializer.Serialize(agent.CapacityOverride, PostgresJson.Ctx.ChannelCapacityOverride);
        var skillsJson = JsonSerializer.Serialize(agent.Skills, PostgresJson.Ctx.IReadOnlyListString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO agents (agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@AgentId, @TenantId, @UserId, @DisplayName, @State, @Capacity::jsonb, @TeamId, @Skills::jsonb, " +
            "@Extension, @SipPassword, @AutoAnswer, @PendingState, @PendingReason, @PendingSince, " +
            "@OfflineSince, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, agent_id) DO UPDATE SET " +
            "  display_name = EXCLUDED.display_name, state = EXCLUDED.state, capacity = EXCLUDED.capacity, " +
            "  team_id = EXCLUDED.team_id, skills = EXCLUDED.skills, " +
            "  extension = EXCLUDED.extension, sip_password = EXCLUDED.sip_password, " +
            "  auto_answer = EXCLUDED.auto_answer, " +
            "  pending_state = EXCLUDED.pending_state, pending_reason = EXCLUDED.pending_reason, " +
            "  pending_since = EXCLUDED.pending_since, offline_since = EXCLUDED.offline_since, " +
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
                p.Add(new NpgsqlParameter("AutoAnswer", NpgsqlDbType.Boolean) { Value = (object?)agent.AutoAnswer ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PendingState", NpgsqlDbType.Integer) { Value = (object?)(int?)agent.PendingState ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PendingReason", NpgsqlDbType.Text) { Value = (object?)agent.PendingReason ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PendingSince", NpgsqlDbType.TimestampTz) { Value = (object?)agent.PendingSince ?? DBNull.Value });
                p.Add(new NpgsqlParameter("OfflineSince", NpgsqlDbType.TimestampTz) { Value = (object?)agent.OfflineSince ?? DBNull.Value });
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

    public async IAsyncEnumerable<Agent> StreamRoutableAgentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The NpgsqlExecutor facade has no streaming primitive, so hand-roll a
        // reader loop and yield row-by-row — the reaper must never buffer every
        // routable agent across every tenant into memory.
        // state IN (1, 2) == { Available, Busy } == AgentStateMachine.IsRoutable.
        await using var cmd = _dataSource.CreateCommand(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE state IN (1, 2)");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return AgentRow.Map(reader).ToAgent();
    }

    public async IAsyncEnumerable<Agent> StreamPendingPauseAgentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // W4 — same hand-rolled streaming reader as StreamRoutableAgentsAsync; the
        // drain sweep must never buffer every pending-pause agent across every tenant
        // into memory. pending_state IS NOT NULL == HasPendingPause.
        await using var cmd = _dataSource.CreateCommand(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE pending_state IS NOT NULL");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return AgentRow.Map(reader).ToAgent();
    }

    public async IAsyncEnumerable<Agent> StreamOfflineAgentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // W5 — same hand-rolled streaming reader as StreamRoutableAgentsAsync; the
        // failover sweep starts from the (few) Offline owners and must never buffer
        // every offline agent across every tenant into memory.
        // state = 0 == AgentState.Offline.
        await using var cmd = _dataSource.CreateCommand(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE state = 0");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return AgentRow.Map(reader).ToAgent();
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
        public bool? auto_answer { get; init; }
        public int? pending_state { get; init; }
        public string? pending_reason { get; init; }
        public DateTime? pending_since { get; init; }
        public DateTime? offline_since { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        // Every SELECT in this store now projects extension / sip_password /
        // auto_answer (the self-scoped /agents/me path needs the SIP secret; auto_answer
        // is not a secret). Agent.SipPassword is [JsonIgnore] so a raw-entity return can
        // never leak it — only the deliberate AgentMeResponseDto copies it.
        public static AgentRow Map(NpgsqlDataReader r) => new()
        {
            agent_id = r.GetString("agent_id"),
            tenant_id = r.GetString("tenant_id"),
            user_id = r.GetString("user_id"),
            display_name = r.GetString("display_name"),
            state = r.GetInt32("state"),
            capacity = r.GetString("capacity"),
            team_id = r.GetStringOrNull("team_id"),
            skills = r.GetString("skills"),
            extension = r.GetStringOrNull("extension"),
            sip_password = r.GetStringOrNull("sip_password"),
            auto_answer = r.IsDBNull(r.GetOrdinal("auto_answer")) ? null : r.GetBoolean("auto_answer"),
            pending_state = r.IsDBNull(r.GetOrdinal("pending_state")) ? (int?)null : r.GetInt32("pending_state"),
            pending_reason = r.GetStringOrNull("pending_reason"),
            pending_since = r.GetDateTimeOrNull("pending_since"),
            offline_since = r.GetDateTimeOrNull("offline_since"),
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
            // W6 — migration 033 normalized every legacy row to '{}', which deserializes
            // to an all-null ChannelCapacityOverride = "inherit the tenant default" on every
            // field. New rows persist only the fields an admin actually overrides.
            CapacityOverride = JsonSerializer.Deserialize(capacity, PostgresJson.Ctx.ChannelCapacityOverride) ?? new ChannelCapacityOverride(),
            TeamId = team_id != null ? EntityId.From(team_id) : null,
            Skills = JsonSerializer.Deserialize(skills, PostgresJson.Ctx.IReadOnlyListString) ?? (IReadOnlyList<string>)[],
            Extension = extension,
            SipPassword = sip_password,
            AutoAnswer = auto_answer,
            PendingState = pending_state is { } ps ? (AgentState)ps : null,
            PendingReason = pending_reason,
            PendingSince = pending_since,
            OfflineSince = offline_since,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
            CreatedBy = created_by,
            UpdatedBy = updated_by,
        };
    }
}
