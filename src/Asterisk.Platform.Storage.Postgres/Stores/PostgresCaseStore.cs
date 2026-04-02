using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresCaseStore : ICaseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCaseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Case?> GetByIdAsync(TenantId tenantId, EntityId caseId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CaseRow>(
            "SELECT case_id, tenant_id, case_number, subject, priority, status, contact_id, " +
            "assigned_agent_id, sla_policy_id, conversation_ids, created_at, updated_at, created_by, updated_by " +
            "FROM cases WHERE tenant_id = @TenantId AND case_id = @CaseId",
            new { TenantId = tenantId.Value, CaseId = caseId.Value });
        return row?.ToCase();
    }

    public async Task<PagedResult<Case>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var offset = (query.Page - 1) * query.PageSize;

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cases WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });

        var rows = await conn.QueryAsync<CaseRow>(
            "SELECT case_id, tenant_id, case_number, subject, priority, status, contact_id, " +
            "assigned_agent_id, sla_policy_id, conversation_ids, created_at, updated_at, created_by, updated_by " +
            "FROM cases WHERE tenant_id = @TenantId " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = offset });

        var items = rows.Select(r => r.ToCase()).ToList();
        return new PagedResult<Case>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Case caseEntity, CancellationToken ct)
    {
        var conversationIdsJson = JsonSerializer.Serialize(
            caseEntity.ConversationIds.Select(id => id.Value).ToList(),
            PostgresJson.Ctx.ListString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO cases (case_id, tenant_id, case_number, subject, priority, status, contact_id, " +
            "assigned_agent_id, sla_policy_id, conversation_ids, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@CaseId, @TenantId, @CaseNumber, @Subject, @Priority, @Status, @ContactId, " +
            "@AssignedAgentId, @SlaPolicyId, @ConversationIds::jsonb, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, case_id) DO UPDATE SET " +
            "  subject = EXCLUDED.subject, priority = EXCLUDED.priority, status = EXCLUDED.status, " +
            "  assigned_agent_id = EXCLUDED.assigned_agent_id, sla_policy_id = EXCLUDED.sla_policy_id, " +
            "  conversation_ids = EXCLUDED.conversation_ids, updated_at = EXCLUDED.updated_at, " +
            "  updated_by = EXCLUDED.updated_by",
            new
            {
                CaseId = caseEntity.CaseId.Value,
                TenantId = caseEntity.TenantId.Value,
                caseEntity.CaseNumber,
                caseEntity.Subject,
                Priority = (int)caseEntity.Priority,
                Status = (int)caseEntity.Status,
                ContactId = caseEntity.ContactId.Value,
                AssignedAgentId = caseEntity.AssignedAgentId?.Value,
                SlaPolicyId = caseEntity.SlaPolicyId?.Value,
                ConversationIds = conversationIdsJson,
                caseEntity.CreatedAt,
                caseEntity.UpdatedAt,
                caseEntity.CreatedBy,
                caseEntity.UpdatedBy,
            });
    }

    private sealed class CaseRow
    {
        public string case_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string case_number { get; init; } = null!;
        public string subject { get; init; } = null!;
        public int priority { get; init; }
        public int status { get; init; }
        public string contact_id { get; init; } = null!;
        public string? assigned_agent_id { get; init; }
        public string? sla_policy_id { get; init; }
        public string conversation_ids { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public Case ToCase()
        {
            var convIdStrings = JsonSerializer.Deserialize(conversation_ids, PostgresJson.Ctx.ListString)
                                ?? [];

            var c = new Case
            {
                CaseId = EntityId.From(case_id),
                TenantId = new TenantId(tenant_id),
                CaseNumber = case_number,
                Subject = subject,
                Priority = (CasePriority)priority,
                Status = (CaseStatus)status,
                ContactId = EntityId.From(contact_id),
                AssignedAgentId = assigned_agent_id != null ? EntityId.From(assigned_agent_id) : null,
                SlaPolicyId = sla_policy_id != null ? EntityId.From(sla_policy_id) : null,
                CreatedAt = created_at,
                UpdatedAt = updated_at,
                CreatedBy = created_by,
                UpdatedBy = updated_by,
            };

            foreach (var id in convIdStrings)
                c.AddConversation(EntityId.From(id));

            return c;
        }
    }
}
