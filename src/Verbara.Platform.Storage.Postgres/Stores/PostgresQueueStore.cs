using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Mirrors the domain entity naming")]
internal sealed class PostgresQueueStore : IQueueStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresQueueStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by " +
            "FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); },
            QueueRow.Map, ct);
        return row?.ToQueue();
    }

    public async Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM queue_configs WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);
        var rows = await _dataSource.QueryListAsync(
            "SELECT queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by " +
            "FROM queue_configs WHERE tenant_id = @TenantId ORDER BY name LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
            QueueRow.Map, ct);
        var items = rows.Select(r => r.ToQueue()).ToList();
        return new PagedResult<Queue>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Queue queue, CancellationToken ct)
    {
        var slaJson = queue.SlaTargets != null
            ? JsonSerializer.Serialize(queue.SlaTargets, PostgresJson.Ctx.SlaPolicyTarget)
            : null;
        var overflowJson = queue.OverflowRule != null
            ? JsonSerializer.Serialize(queue.OverflowRule, PostgresJson.Ctx.QueueOverflowRule)
            : null;
        var hoursJson = queue.Hours != null
            ? JsonSerializer.Serialize(queue.Hours, PostgresJson.Ctx.HoursOfOperation)
            : null;
        var wrapUpJson = JsonSerializer.Serialize(queue.WrapUp, PostgresJson.Ctx.WrapUpConfig);
        var skillsJson = JsonSerializer.Serialize(queue.RequiredSkills, PostgresJson.Ctx.IReadOnlyListString);

        try
        {
            await _dataSource.ExecuteAsync(
                "INSERT INTO queue_configs (queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
                "required_skills, created_at, updated_at, created_by, updated_by) " +
                "VALUES (@QueueId, @TenantId, @Name, @IsActive, @MaxWaiting, @SlaTargets::jsonb, @OverflowRule::jsonb, " +
                "@Hours::jsonb, @WrapUp::jsonb, @RequiredSkills::jsonb, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
                "ON CONFLICT (tenant_id, queue_id) DO UPDATE SET " +
                "  name = EXCLUDED.name, is_active = EXCLUDED.is_active, max_waiting = EXCLUDED.max_waiting, " +
                "  sla_targets = EXCLUDED.sla_targets, overflow_rule = EXCLUDED.overflow_rule, hours = EXCLUDED.hours, " +
                "  wrap_up = EXCLUDED.wrap_up, required_skills = EXCLUDED.required_skills, " +
                "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
                p =>
                {
                    p.Add(new NpgsqlParameter("QueueId", queue.QueueId.Value));
                    p.Add(new NpgsqlParameter("TenantId", queue.TenantId.Value));
                    p.Add(new NpgsqlParameter("Name", queue.Name));
                    p.Add(new NpgsqlParameter("IsActive", queue.IsActive));
                    p.Add(new NpgsqlParameter("MaxWaiting", (object?)queue.MaxWaiting ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("SlaTargets", (object?)slaJson ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("OverflowRule", (object?)overflowJson ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("Hours", (object?)hoursJson ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("WrapUp", wrapUpJson));
                    p.Add(new NpgsqlParameter("RequiredSkills", skillsJson));
                    p.Add(new NpgsqlParameter("CreatedAt", queue.CreatedAt));
                    p.Add(new NpgsqlParameter("UpdatedAt", (object?)queue.UpdatedAt ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("CreatedBy", (object?)queue.CreatedBy ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("UpdatedBy", (object?)queue.UpdatedBy ?? DBNull.Value));
                },
                ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // v1.14.3 (R5.5 P0 finding #4 fix). Defensive translation: even
            // though the current 001_InitialSchema doesn't put a UNIQUE on
            // (tenant_id, name) for queue_configs, a downstream migration
            // or operator-applied DDL could add one. Without this catch,
            // any future 23505 from this path would 500 with the raw
            // Postgres constraint name in the response body.
            var field = ex.ConstraintName?.Contains("name", StringComparison.OrdinalIgnoreCase) == true
                ? "name"
                : null;
            throw new EntityAlreadyExistsException("queue", field, ex);
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); },
            ct);
    }

    private sealed class QueueRow
    {
        public string queue_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public bool is_active { get; init; }
        public int? max_waiting { get; init; }
        public string? sla_targets { get; init; }
        public string? overflow_rule { get; init; }
        public string? hours { get; init; }
        public string? wrap_up { get; init; }
        public string required_skills { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public static QueueRow Map(NpgsqlDataReader r) => new()
        {
            queue_id = r.GetString("queue_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            is_active = r.GetBoolean("is_active"),
            max_waiting = r.GetInt32OrNull("max_waiting"),
            sla_targets = r.GetStringOrNull("sla_targets"),
            overflow_rule = r.GetStringOrNull("overflow_rule"),
            hours = r.GetStringOrNull("hours"),
            wrap_up = r.GetStringOrNull("wrap_up"),
            required_skills = r.GetString("required_skills"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };

        public Queue ToQueue() => new()
        {
            QueueId = EntityId.From(queue_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            IsActive = is_active,
            MaxWaiting = max_waiting,
            SlaTargets = sla_targets != null
                ? JsonSerializer.Deserialize(sla_targets, PostgresJson.Ctx.SlaPolicyTarget)
                : null,
            OverflowRule = overflow_rule != null
                ? JsonSerializer.Deserialize(overflow_rule, PostgresJson.Ctx.QueueOverflowRule)
                : null,
            Hours = hours != null
                ? JsonSerializer.Deserialize(hours, PostgresJson.Ctx.HoursOfOperation)
                : null,
            WrapUp = wrap_up != null
                ? JsonSerializer.Deserialize(wrap_up, PostgresJson.Ctx.WrapUpConfig) ?? new WrapUpConfig()
                : new WrapUpConfig(),
            RequiredSkills = JsonSerializer.Deserialize(required_skills, PostgresJson.Ctx.IReadOnlyListString)
                             ?? (IReadOnlyList<string>)[],
            CreatedAt = created_at,
            UpdatedAt = updated_at,
            CreatedBy = created_by,
            UpdatedBy = updated_by,
        };
    }
}
