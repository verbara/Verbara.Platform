using System.Data;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed <see cref="ISkillCatalogStore"/> (routing-skills-stay-in-their-tenant, design D1, D2) on
/// <c>routing_skills</c> and <c>agent_skill_proficiencies</c> (migration 032). Every statement carries
/// <c>tenant_id = @TenantId</c>.
/// </summary>
/// <remarks>
/// <para>
/// The write statements are the texts measured on PostgreSQL 18 (card PLT-SKL-01); a change to any of them
/// invalidates that measurement. Each write of one assignment key runs in a READ COMMITTED transaction that first
/// takes the key's transaction-scoped advisory lock (<see cref="KeyLockSql"/>) and holds it to commit, so the
/// replaced value the assign's <c>old</c> CTE reads, and the row the unassign's <c>DELETE … RETURNING</c> returns,
/// are the ones that write changed: no other write of the key is in flight when its snapshot is taken.
/// </para>
/// <para>
/// A skill delete locks the definition row first, then the key of every holder in <c>agent_id</c> order, and deletes
/// only the assignments of the agents whose key it holds. No other writer holds more than one key lock, so no lock
/// cycle can form. An agent delete takes no key lock: its foreign-key cascade removes the agent's rows, and an assign
/// that races it fails with <c>23503</c>, reported as <see cref="SkillAssignOutcome.NoSuchAgent"/>.
/// </para>
/// </remarks>
internal sealed class PostgresSkillCatalogStore : ISkillCatalogStore
{
    /// <summary>The composite foreign key to <c>agents</c> (migration 032).</summary>
    internal const string AgentForeignKey = "fk_agent_skill_proficiencies_agent";

    // S-upsert-skill
    internal const string UpsertSkillSql =
        """
        INSERT INTO routing_skills (tenant_id, name, category, description, created_at, updated_at)
        VALUES (@TenantId, @Name, @Category, @Description, now(), now())
        ON CONFLICT (tenant_id, lower(name)) DO UPDATE
        SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description, updated_at = now()
        RETURNING name, category, description
        """;

    // S-key-lock
    internal const string KeyLockSql =
        """
        SELECT pg_advisory_xact_lock(hashtextextended(@TenantId || '/' || @AgentId || '/' || lower(@SkillName), 0))
        """;

    // S-assign (spec-audit F1: the previous skill_name comes back with the previous proficiency)
    internal const string AssignSql =
        """
        WITH old AS (SELECT skill_name, proficiency FROM agent_skill_proficiencies
                     WHERE tenant_id = @TenantId AND agent_id = @AgentId AND lower(skill_name) = lower(@SkillName))
        INSERT INTO agent_skill_proficiencies (tenant_id, agent_id, skill_name, proficiency, created_at, updated_at)
        VALUES (@TenantId, @AgentId, @SkillName, @Proficiency, now(), now())
        ON CONFLICT (tenant_id, agent_id, lower(skill_name)) DO UPDATE
        SET skill_name = EXCLUDED.skill_name, proficiency = EXCLUDED.proficiency, updated_at = now()
        RETURNING (SELECT skill_name FROM old) AS replaced_skill_name, (SELECT proficiency FROM old) AS replaced_proficiency,
                  skill_name, proficiency
        """;

    // S-unassign
    internal const string UnassignSql =
        """
        DELETE FROM agent_skill_proficiencies
        WHERE tenant_id = @TenantId AND agent_id = @AgentId AND lower(skill_name) = lower(@SkillName)
        RETURNING skill_name, proficiency
        """;

    // S-remove-skill (1): lock the definition row; none → NotFound.
    internal const string RemoveSkillLockDefinitionSql =
        """
        SELECT name FROM routing_skills WHERE tenant_id = @TenantId AND lower(name) = lower(@Name) FOR UPDATE
        """;

    // S-remove-skill (2): take the key lock of every holder, in agent_id order; returns @LockedAgentIds.
    internal const string RemoveSkillLockHoldersSql =
        """
        SELECT k.agent_id FROM (SELECT DISTINCT agent_id FROM agent_skill_proficiencies
                                WHERE tenant_id = @TenantId AND lower(skill_name) = lower(@Name) ORDER BY agent_id) k
        CROSS JOIN LATERAL (SELECT pg_advisory_xact_lock(hashtextextended(@TenantId || '/' || k.agent_id || '/' || lower(@Name), 0))) l
        ORDER BY k.agent_id
        """;

    // S-remove-skill (4a): only the assignments of the agents whose key is held.
    internal const string RemoveSkillDeleteAssignmentsSql =
        """
        DELETE FROM agent_skill_proficiencies
        WHERE tenant_id = @TenantId AND lower(skill_name) = lower(@Name) AND agent_id = ANY(@LockedAgentIds)
        """;

    // S-remove-skill (4b)
    internal const string RemoveSkillDeleteDefinitionSql =
        """
        DELETE FROM routing_skills WHERE tenant_id = @TenantId AND lower(name) = lower(@Name)
        """;

    private const string ProficiencyColumns = "agent_id, skill_name, proficiency";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSkillCatalogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Test seam: when set, every write transaction awaits it after its last statement and before its commit, with
    /// its locks held, so a test can sequence another writer against the uncommitted write.
    /// </summary>
    internal Func<Task>? BeforeCommit { get; init; }

    public async Task<IReadOnlyList<SkillDefinition>> ListSkillsAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT name, category, description FROM routing_skills WHERE tenant_id = @TenantId ORDER BY lower(name), name",
            p => p.Add(Text("TenantId", tenantId.Value)),
            SkillRow.Map, ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToModel())];
    }

    public async Task<SkillDefinition> UpsertSkillAsync(TenantId tenantId, SkillDefinition skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var row = await _dataSource.QuerySingleAsync(
            UpsertSkillSql,
            p =>
            {
                p.Add(Text("TenantId", tenantId.Value));
                p.Add(Text("Name", skill.Name));
                p.Add(NullableText("Category", skill.Category));
                p.Add(NullableText("Description", skill.Description));
            },
            SkillRow.Map, ct).ConfigureAwait(false);
        return row.ToModel();
    }

    public async Task<SkillRemoval> RemoveSkillAsync(TenantId tenantId, string name, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await using (var lockDefinition = Command(conn, tx, RemoveSkillLockDefinitionSql))
        {
            lockDefinition.Parameters.Add(Text("TenantId", tenantId.Value));
            lockDefinition.Parameters.Add(Text("Name", name));
            if (await lockDefinition.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return SkillRemoval.NotFound;
            }
        }

        var lockedAgentIds = new List<string>();
        await using (var lockHolders = Command(conn, tx, RemoveSkillLockHoldersSql))
        {
            lockHolders.Parameters.Add(Text("TenantId", tenantId.Value));
            lockHolders.Parameters.Add(Text("Name", name));
            await using var reader = await lockHolders.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                lockedAgentIds.Add(reader.GetString("agent_id"));
        }

        if (!force && lockedAgentIds.Count > 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return SkillRemoval.InUse(lockedAgentIds.Count);
        }

        await using (var deleteAssignments = Command(conn, tx, RemoveSkillDeleteAssignmentsSql))
        {
            deleteAssignments.Parameters.Add(Text("TenantId", tenantId.Value));
            deleteAssignments.Parameters.Add(Text("Name", name));
            deleteAssignments.Parameters.Add(new NpgsqlParameter("LockedAgentIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = lockedAgentIds.ToArray(),
            });
            await deleteAssignments.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var deleteDefinition = Command(conn, tx, RemoveSkillDeleteDefinitionSql))
        {
            deleteDefinition.Parameters.Add(Text("TenantId", tenantId.Value));
            deleteDefinition.Parameters.Add(Text("Name", name));
            await deleteDefinition.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await CommitAsync(tx, ct).ConfigureAwait(false);
        return SkillRemoval.Removed;
    }

    public async Task<IReadOnlyList<AgentSkillProficiency>> ListAgentsWithSkillAsync(
        TenantId tenantId, string skillName, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {ProficiencyColumns} FROM agent_skill_proficiencies " +
            "WHERE tenant_id = @TenantId AND lower(skill_name) = lower(@SkillName) ORDER BY agent_id",
            p =>
            {
                p.Add(Text("TenantId", tenantId.Value));
                p.Add(Text("SkillName", skillName));
            },
            ProficiencyRow.Map, ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToModel())];
    }

    public async Task<IReadOnlyList<AgentSkillProficiency>> ListAgentSkillsAsync(
        TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {ProficiencyColumns} FROM agent_skill_proficiencies " +
            "WHERE tenant_id = @TenantId AND agent_id = @AgentId ORDER BY lower(skill_name), skill_name",
            p =>
            {
                p.Add(Text("TenantId", tenantId.Value));
                p.Add(Text("AgentId", agentId.Value));
            },
            ProficiencyRow.Map, ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToModel())];
    }

    public async Task<SkillAssignment> AssignAsync(TenantId tenantId, AgentSkillProficiency assignment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await TakeKeyLockAsync(conn, tx, tenantId, assignment.AgentId, assignment.SkillName, ct).ConfigureAwait(false);

        AssignRow row;
        try
        {
            await using var assign = Command(conn, tx, AssignSql);
            assign.Parameters.Add(Text("TenantId", tenantId.Value));
            assign.Parameters.Add(Text("AgentId", assignment.AgentId.Value));
            assign.Parameters.Add(Text("SkillName", assignment.SkillName));
            assign.Parameters.Add(new NpgsqlParameter("Proficiency", NpgsqlDbType.Integer) { Value = assignment.Proficiency });
            await using var reader = await assign.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            row = AssignRow.Map(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                                            && string.Equals(ex.ConstraintName, AgentForeignKey, StringComparison.Ordinal))
        {
            // No such agent of the tenant: it never existed, or a delete committed before this write (design D3).
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return SkillAssignment.NoSuchAgent;
        }

        await CommitAsync(tx, ct).ConfigureAwait(false);
        var stored = new AgentSkillProficiency(assignment.AgentId, row.skill_name, row.proficiency);
        var replaced = row.replaced_skill_name is { } replacedName && row.replaced_proficiency is { } replacedProficiency
            ? new AgentSkillProficiency(assignment.AgentId, replacedName, replacedProficiency)
            : null;
        return SkillAssignment.Assigned(stored, replaced);
    }

    public async Task<AgentSkillProficiency?> UnassignAsync(
        TenantId tenantId, EntityId agentId, string skillName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(skillName);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await TakeKeyLockAsync(conn, tx, tenantId, agentId, skillName, ct).ConfigureAwait(false);

        AgentSkillProficiency? deleted = null;
        await using (var unassign = Command(conn, tx, UnassignSql))
        {
            unassign.Parameters.Add(Text("TenantId", tenantId.Value));
            unassign.Parameters.Add(Text("AgentId", agentId.Value));
            unassign.Parameters.Add(Text("SkillName", skillName));
            await using var reader = await unassign.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                deleted = new AgentSkillProficiency(agentId, reader.GetString("skill_name"), reader.GetInt32("proficiency"));
        }

        await CommitAsync(tx, ct).ConfigureAwait(false);
        return deleted;
    }

    public async Task<IReadOnlyList<AgentSkillProficiency>> ListProficienciesAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {ProficiencyColumns} FROM agent_skill_proficiencies " +
            "WHERE tenant_id = @TenantId ORDER BY agent_id, lower(skill_name), skill_name",
            p => p.Add(Text("TenantId", tenantId.Value)),
            ProficiencyRow.Map, ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToModel())];
    }

    private static async Task TakeKeyLockAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TenantId tenantId, EntityId agentId, string skillName, CancellationToken ct)
    {
        await using var keyLock = Command(conn, tx, KeyLockSql);
        keyLock.Parameters.Add(Text("TenantId", tenantId.Value));
        keyLock.Parameters.Add(Text("AgentId", agentId.Value));
        keyLock.Parameters.Add(Text("SkillName", skillName));
        await keyLock.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task CommitAsync(NpgsqlTransaction tx, CancellationToken ct)
    {
        if (BeforeCommit is { } beforeCommit)
            await beforeCommit().ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql) =>
        new(sql, conn, tx);

    private static NpgsqlParameter Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { Value = value };

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private sealed class SkillRow
    {
        public string name { get; init; } = null!;
        public string? category { get; init; }
        public string? description { get; init; }

        public static SkillRow Map(NpgsqlDataReader r) => new()
        {
            name = r.GetString("name"),
            category = r.GetStringOrNull("category"),
            description = r.GetStringOrNull("description"),
        };

        public SkillDefinition ToModel() => new(name, category, description);
    }

    private sealed class ProficiencyRow
    {
        public string agent_id { get; init; } = null!;
        public string skill_name { get; init; } = null!;
        public int proficiency { get; init; }

        public static ProficiencyRow Map(NpgsqlDataReader r) => new()
        {
            agent_id = r.GetString("agent_id"),
            skill_name = r.GetString("skill_name"),
            proficiency = r.GetInt32("proficiency"),
        };

        public AgentSkillProficiency ToModel() => new(EntityId.From(agent_id), skill_name, proficiency);
    }

    private sealed class AssignRow
    {
        public string? replaced_skill_name { get; init; }
        public int? replaced_proficiency { get; init; }
        public string skill_name { get; init; } = null!;
        public int proficiency { get; init; }

        public static AssignRow Map(NpgsqlDataReader r) => new()
        {
            replaced_skill_name = r.GetStringOrNull("replaced_skill_name"),
            replaced_proficiency = r.GetInt32OrNull("replaced_proficiency"),
            skill_name = r.GetString("skill_name"),
            proficiency = r.GetInt32("proficiency"),
        };
    }
}
