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

    /// <summary>The unique index on (tenant_id, user_id) that migration 018 creates.</summary>
    internal const string UserUniqueIndex = "ux_agents_tenant_user";

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

    public async Task<IReadOnlyList<Agent>> GetByIdsAsync(TenantId tenantId, IReadOnlyCollection<EntityId> agentIds, CancellationToken ct)
    {
        if (agentIds.Count == 0)
            return [];

        var rows = await _dataSource.QueryListAsync(
            "SELECT agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
            "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
            "offline_since, created_at, updated_at, created_by, updated_by " +
            "FROM agents WHERE tenant_id = @TenantId AND agent_id = ANY(@Ids)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Ids", agentIds.Select(id => id.Value).ToArray()));
            },
            AgentRow.Map, ct);
        return rows.Select(r => r.ToAgent()).ToList();
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
            $"FROM agents WHERE {where} ORDER BY display_name, agent_id LIMIT @Limit OFFSET @Offset",
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

    // licensed-agent-metering (design D5) — an agent row is inserted and deleted only inside the licensed-agent
    // writer's transaction, together with its ledger row (InsertAsync / DeleteAsync overloads below). SaveAsync
    // updates an existing agent and never creates one: a presence or admin save racing a delete changes no
    // row instead of bringing the agent back without a ledger row.
    internal const string InsertSql =
        "INSERT INTO agents (agent_id, tenant_id, user_id, display_name, state, capacity, team_id, skills, " +
        "extension, sip_password, auto_answer, pending_state, pending_reason, pending_since, " +
        "offline_since, created_at, updated_at, created_by, updated_by) " +
        "VALUES (@AgentId, @TenantId, @UserId, @DisplayName, @State, @Capacity::jsonb, @TeamId, @Skills::jsonb, " +
        "@Extension, @SipPassword, @AutoAnswer, @PendingState, @PendingReason, @PendingSince, " +
        "@OfflineSince, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy)";

    internal const string DeleteSql =
        "DELETE FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId";

    private const string UpdateSql =
        "UPDATE agents SET " +
        "  display_name = @DisplayName, state = @State, capacity = @Capacity::jsonb, " +
        "  team_id = @TeamId, skills = @Skills::jsonb, " +
        "  extension = @Extension, sip_password = @SipPassword, " +
        "  auto_answer = @AutoAnswer, " +
        "  pending_state = @PendingState, pending_reason = @PendingReason, " +
        "  pending_since = @PendingSince, offline_since = @OfflineSince, " +
        "  updated_at = @UpdatedAt, updated_by = @UpdatedBy " +
        "WHERE tenant_id = @TenantId AND agent_id = @AgentId";

    /// <summary>
    /// Updates an existing agent. An agent that does not exist is not created: creation goes through the
    /// licensed-agent writer (<see cref="InsertAsync"/>), which records it in the ledger.
    /// </summary>
    public Task SaveAsync(Agent agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return _dataSource.ExecuteAsync(UpdateSql, p => BindAgent(p, agent), ct);
    }

    /// <summary>
    /// Inserts <paramref name="agent"/> inside <paramref name="tx"/>. Only the licensed-agent writer calls it,
    /// with the agent's ledger row in the same transaction. A second agent for a user who already owns one
    /// is refused by <see cref="UserUniqueIndex"/> and surfaces as <see cref="EntityAlreadyExistsException"/>.
    /// </summary>
    internal static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Agent agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(agent);
        try
        {
            await conn.ExecuteAsync(InsertSql, p => BindAgent(p, agent), tx, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                           && ex.ConstraintName == UserUniqueIndex)
        {
            // licensed-agent-metering (D3) — a second agent for a user who already owns one in the tenant.
            // Race-free: of two concurrent creations exactly one row lands; the endpoint answers 409.
            throw new EntityAlreadyExistsException("agent", "user_id", ex);
        }
    }

    /// <summary>
    /// Deletes an agent row inside <paramref name="tx"/>; returns the rows deleted. Only the licensed-agent
    /// writer calls it, with the agent's ledger row in the same transaction.
    /// </summary>
    internal static Task<int> DeleteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        return conn.ExecuteAsync(
            DeleteSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agentId.Value });
            },
            tx, ct);
    }

    /// <summary>
    /// The standalone delete keeps the store contract and delegates to the transaction overload in a
    /// transaction of its own, with no ledger row. Production code deletes agents through the licensed-agent
    /// writer; a guard test holds every production caller to that.
    /// </summary>
    public async Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await DeleteAsync(conn, tx, tenantId, agentId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static void BindAgent(NpgsqlParameterCollection p, Agent agent)
    {
        var capacityJson = JsonSerializer.Serialize(agent.CapacityOverride, PostgresJson.Ctx.ChannelCapacityOverride);
        var skillsJson = JsonSerializer.Serialize(agent.Skills, PostgresJson.Ctx.IReadOnlyListString);

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
