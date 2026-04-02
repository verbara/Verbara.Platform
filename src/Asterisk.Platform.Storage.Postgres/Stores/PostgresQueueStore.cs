using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Storage.Postgres.Stores;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Mirrors the domain entity naming")]
internal sealed class PostgresQueueStore : IQueueStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresQueueStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QueueRow>(
            "SELECT queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by " +
            "FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            new { TenantId = tenantId.Value, QueueId = queueId.Value });
        return row?.ToQueue();
    }

    public async Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM queue_configs WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
        var rows = await conn.QueryAsync<QueueRow>(
            "SELECT queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by " +
            "FROM queue_configs WHERE tenant_id = @TenantId ORDER BY name LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset });
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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO queue_configs (queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@QueueId, @TenantId, @Name, @IsActive, @MaxWaiting, @SlaTargets::jsonb, @OverflowRule::jsonb, " +
            "@Hours::jsonb, @WrapUp::jsonb, @RequiredSkills::jsonb, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, queue_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, is_active = EXCLUDED.is_active, max_waiting = EXCLUDED.max_waiting, " +
            "  sla_targets = EXCLUDED.sla_targets, overflow_rule = EXCLUDED.overflow_rule, hours = EXCLUDED.hours, " +
            "  wrap_up = EXCLUDED.wrap_up, required_skills = EXCLUDED.required_skills, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                QueueId = queue.QueueId.Value,
                TenantId = queue.TenantId.Value,
                queue.Name,
                queue.IsActive,
                queue.MaxWaiting,
                SlaTargets = slaJson,
                OverflowRule = overflowJson,
                Hours = hoursJson,
                WrapUp = wrapUpJson,
                RequiredSkills = skillsJson,
                queue.CreatedAt,
                queue.UpdatedAt,
                queue.CreatedBy,
                queue.UpdatedBy,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            new { TenantId = tenantId.Value, QueueId = queueId.Value });
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
