using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresCaseStore : ICaseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCaseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Case?> GetByIdAsync(TenantId tenantId, EntityId caseId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT case_id, tenant_id, case_number, subject, priority, status, contact_id, " +
            "assigned_agent_id, sla_policy_id, conversation_ids, created_at, updated_at, created_by, updated_by " +
            "FROM cases WHERE tenant_id = @TenantId AND case_id = @CaseId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("CaseId", caseId.Value)); },
            CaseRow.Map, ct);
        return row?.ToCase();
    }

    public async Task<PagedResult<Case>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var offset = (query.Page - 1) * query.PageSize;

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM cases WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT case_id, tenant_id, case_number, subject, priority, status, contact_id, " +
            "assigned_agent_id, sla_policy_id, conversation_ids, created_at, updated_at, created_by, updated_by " +
            "FROM cases WHERE tenant_id = @TenantId " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", offset)); },
            CaseRow.Map, ct);

        var items = rows.Select(r => r.ToCase()).ToList();
        return new PagedResult<Case>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Case caseEntity, CancellationToken ct)
    {
        var conversationIdsJson = JsonSerializer.Serialize(
            caseEntity.ConversationIds.Select(id => id.Value).ToList(),
            PostgresJson.Ctx.ListString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO cases (case_id, tenant_id, case_number, subject, priority, status, contact_id, " +
            "assigned_agent_id, sla_policy_id, conversation_ids, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@CaseId, @TenantId, @CaseNumber, @Subject, @Priority, @Status, @ContactId, " +
            "@AssignedAgentId, @SlaPolicyId, @ConversationIds::jsonb, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, case_id) DO UPDATE SET " +
            "  subject = EXCLUDED.subject, priority = EXCLUDED.priority, status = EXCLUDED.status, " +
            "  assigned_agent_id = EXCLUDED.assigned_agent_id, sla_policy_id = EXCLUDED.sla_policy_id, " +
            "  conversation_ids = EXCLUDED.conversation_ids, updated_at = EXCLUDED.updated_at, " +
            "  updated_by = EXCLUDED.updated_by",
            p =>
            {
                p.Add(new NpgsqlParameter("CaseId", caseEntity.CaseId.Value));
                p.Add(new NpgsqlParameter("TenantId", caseEntity.TenantId.Value));
                p.Add(new NpgsqlParameter("CaseNumber", caseEntity.CaseNumber));
                p.Add(new NpgsqlParameter("Subject", caseEntity.Subject));
                p.Add(new NpgsqlParameter("Priority", (int)caseEntity.Priority));
                p.Add(new NpgsqlParameter("Status", (int)caseEntity.Status));
                p.Add(new NpgsqlParameter("ContactId", caseEntity.ContactId.Value));
                p.Add(new NpgsqlParameter("AssignedAgentId", (object?)caseEntity.AssignedAgentId?.Value ?? DBNull.Value));
                p.Add(new NpgsqlParameter("SlaPolicyId", (object?)caseEntity.SlaPolicyId?.Value ?? DBNull.Value));
                p.Add(new NpgsqlParameter("ConversationIds", conversationIdsJson));
                p.Add(new NpgsqlParameter("CreatedAt", caseEntity.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", (object?)caseEntity.UpdatedAt ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedBy", (object?)caseEntity.CreatedBy ?? DBNull.Value));
                p.Add(new NpgsqlParameter("UpdatedBy", (object?)caseEntity.UpdatedBy ?? DBNull.Value));
            },
            ct);
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

        public static CaseRow Map(NpgsqlDataReader r) => new()
        {
            case_id = r.GetString("case_id"),
            tenant_id = r.GetString("tenant_id"),
            case_number = r.GetString("case_number"),
            subject = r.GetString("subject"),
            priority = r.GetInt32("priority"),
            status = r.GetInt32("status"),
            contact_id = r.GetString("contact_id"),
            assigned_agent_id = r.GetStringOrNull("assigned_agent_id"),
            sla_policy_id = r.GetStringOrNull("sla_policy_id"),
            conversation_ids = r.GetString("conversation_ids"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };

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
