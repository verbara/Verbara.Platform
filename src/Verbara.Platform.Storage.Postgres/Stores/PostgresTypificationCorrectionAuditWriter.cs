using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Audit;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres implementation of <see cref="ITypificationCorrectionAuditWriter"/>
/// (audit-trail-integrity-fixes, fix 1): the correction insert, the submission UPSERT (status
/// pointers flipped), and the audit insert all run inside ONE <see cref="NpgsqlTransaction"/> —
/// either all three commit, or a fault rolls all three back. Mirrors the exact SQL/param-binding
/// <c>PostgresTypificationSubmissionCorrectionStore</c>, <c>PostgresTypificationSubmissionStore</c>,
/// and <c>PostgresAuditStore</c> already use standalone, transaction-scoped via the
/// <c>NpgsqlConnection.ExecuteAsync(sql, bind, transaction, ct)</c> overload (the same pattern
/// <c>PostgresCreditLedgerStore</c> uses for its multi-row atomic writes).
/// </summary>
internal sealed class PostgresTypificationCorrectionAuditWriter : ITypificationCorrectionAuditWriter
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTypificationCorrectionAuditWriter(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task CommitAsync(
        TypificationSubmissionCorrection correction,
        TypificationSubmission submission,
        AuditEntry auditEntry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(auditEntry);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await InsertCorrectionAsync(conn, tx, correction, ct);
        await SaveSubmissionAsync(conn, tx, submission, ct);
        await InsertAuditEntryAsync(conn, tx, auditEntry, ct);

        await tx.CommitAsync(ct);
    }

    // Mirrors PostgresTypificationSubmissionCorrectionStore.InsertAsync — same SQL/params, bound
    // to the shared transaction instead of running standalone.
    private static async Task InsertCorrectionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TypificationSubmissionCorrection record, CancellationToken ct)
    {
        IReadOnlyList<string> nodePath = record.CorrectedNodePath.Select(n => n.Value).ToList();
        var nodePathJson = JsonSerializer.Serialize(nodePath, PostgresJson.Ctx.IReadOnlyListString);

        await conn.ExecuteAsync(
            "INSERT INTO typification_submission_corrections " +
            "(tenant_id, conversation_id, corrected_leaf_node_id, corrected_node_path, corrected_by_user_id, corrected_at) " +
            "VALUES (@TenantId, @ConversationId, @CorrectedLeafNodeId, @CorrectedNodePath::jsonb, @CorrectedByUserId, @CorrectedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", record.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", record.ConversationId.Value));
                p.Add(new NpgsqlParameter("CorrectedLeafNodeId", record.CorrectedLeafNodeId.Value));
                p.Add(new NpgsqlParameter("CorrectedNodePath", nodePathJson));
                p.Add(new NpgsqlParameter("CorrectedByUserId", record.CorrectedByUserId));
                p.Add(new NpgsqlParameter("CorrectedAt", NpgsqlDbType.TimestampTz) { Value = record.CorrectedAt.UtcDateTime });
            },
            tx, ct);
    }

    // Mirrors PostgresTypificationSubmissionStore.SaveAsync — same SQL/params, bound to the
    // shared transaction. The correction endpoint only ever flips CorrectionState/CorrectedAt (the
    // AI fields stay byte-identical), but the full UPSERT is reused unchanged for a single source
    // of truth on the column list.
    private static async Task SaveSubmissionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TypificationSubmission submission, CancellationToken ct)
    {
        IReadOnlyList<string> nodePath = submission.SelectedNodePath.Select(n => n.Value).ToList();
        var nodePathJson = JsonSerializer.Serialize(nodePath, PostgresJson.Ctx.IReadOnlyListString);
        var fieldValuesJson = JsonSerializer.Serialize(submission.FieldValues, PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        var suggestedNodePathJson = submission.SuggestedNodePath is not null
            ? JsonSerializer.Serialize(submission.SuggestedNodePath, PostgresJson.Ctx.IReadOnlyListString)
            : null;

        await conn.ExecuteAsync(
            "INSERT INTO typification_submissions " +
            "(tenant_id, conversation_id, agent_id, schema_id, schema_version, selected_node_path, leaf_node_id, " +
            " field_values, notes, ai_suggested, ai_confidence, ai_accepted, source, duration_ms, completed_at, " +
            " suggested_leaf_node_id, suggested_node_path, " +
            " autonomous_actor_id, correction_state, corrected_at) " +
            "VALUES (@TenantId, @ConversationId, @AgentId, @SchemaId, @SchemaVersion, @SelectedNodePath::jsonb, @LeafNodeId, " +
            " @FieldValues::jsonb, @Notes, @AiSuggested, @AiConfidence, @AiAccepted, @Source, @DurationMs, @CompletedAt, " +
            " @SuggestedLeafNodeId, @SuggestedNodePath::jsonb, " +
            " @AutonomousActorId, @CorrectionState, @CorrectedAt) " +
            "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
            "  agent_id = EXCLUDED.agent_id, schema_id = EXCLUDED.schema_id, schema_version = EXCLUDED.schema_version, " +
            "  selected_node_path = EXCLUDED.selected_node_path, leaf_node_id = EXCLUDED.leaf_node_id, " +
            "  field_values = EXCLUDED.field_values, notes = EXCLUDED.notes, ai_suggested = EXCLUDED.ai_suggested, " +
            "  ai_confidence = EXCLUDED.ai_confidence, ai_accepted = EXCLUDED.ai_accepted, source = EXCLUDED.source, " +
            "  duration_ms = EXCLUDED.duration_ms, completed_at = EXCLUDED.completed_at, " +
            "  suggested_leaf_node_id = EXCLUDED.suggested_leaf_node_id, " +
            "  suggested_node_path = EXCLUDED.suggested_node_path, " +
            "  autonomous_actor_id = EXCLUDED.autonomous_actor_id, correction_state = EXCLUDED.correction_state, " +
            "  corrected_at = EXCLUDED.corrected_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", submission.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", submission.ConversationId.Value));
                p.Add(new NpgsqlParameter("AgentId", submission.AgentId.Value));
                p.Add(new NpgsqlParameter("SchemaId", submission.SchemaId.Value));
                p.Add(new NpgsqlParameter("SchemaVersion", submission.SchemaVersion));
                p.Add(new NpgsqlParameter("SelectedNodePath", nodePathJson));
                p.Add(new NpgsqlParameter("LeafNodeId", submission.LeafNodeId.Value));
                p.Add(new NpgsqlParameter("FieldValues", fieldValuesJson));
                p.Add(new NpgsqlParameter("Notes", NpgsqlDbType.Text) { Value = (object?)submission.Notes ?? DBNull.Value });
                p.Add(new NpgsqlParameter("AiSuggested", submission.AiSuggested));
                p.Add(new NpgsqlParameter("AiConfidence", NpgsqlDbType.Double) { Value = (object?)submission.AiConfidence ?? DBNull.Value });
                p.Add(new NpgsqlParameter("AiAccepted", NpgsqlDbType.Boolean) { Value = (object?)submission.AiAccepted ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Source", submission.Source.ToString()));
                p.Add(new NpgsqlParameter("DurationMs", (long)submission.Duration.TotalMilliseconds));
                p.Add(new NpgsqlParameter("CompletedAt", submission.CompletedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("SuggestedLeafNodeId", NpgsqlDbType.Text) { Value = (object?)submission.SuggestedLeafNodeId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SuggestedNodePath", NpgsqlDbType.Text) { Value = (object?)suggestedNodePathJson ?? DBNull.Value });
                p.Add(new NpgsqlParameter("AutonomousActorId", NpgsqlDbType.Text) { Value = (object?)submission.AutonomousActorId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CorrectionState", NpgsqlDbType.Smallint) { Value = (short)submission.CorrectionState });
                p.Add(new NpgsqlParameter("CorrectedAt", NpgsqlDbType.TimestampTz) { Value = (object?)submission.CorrectedAt?.UtcDateTime ?? DBNull.Value });
            },
            tx, ct);
    }

    // Mirrors PostgresAuditStore.SaveAsync — same SQL/params, bound to the shared transaction.
    private static async Task InsertAuditEntryAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry entry, CancellationToken ct)
    {
        var metadataJson = entry.Metadata != null
            ? JsonSerializer.Serialize(entry.Metadata, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
            : null;

        var beforeJson = SerializeChange(entry.Changes?.Before);
        var afterJson = SerializeChange(entry.Changes?.After);

        await conn.ExecuteAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "performed_by, details, occurred_at, impersonator_id, " +
            "category, severity, actor_type, before_json, after_json, integrity_hash, retain_until) " +
            "VALUES (@EntryId, @TenantId, @Action, @EntityType, @EntityId, " +
            "@PerformedBy, @Details::jsonb, @OccurredAt, @ImpersonatorId, " +
            "@Category, @Severity, @ActorType, @BeforeJson::jsonb, @AfterJson::jsonb, @IntegrityHash, @RetainUntil)",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", entry.EntryId.Value));
                p.Add(new NpgsqlParameter("TenantId", entry.TenantId.Value));
                p.Add(new NpgsqlParameter("Action", entry.Action));
                p.Add(new NpgsqlParameter("EntityType", NpgsqlDbType.Text) { Value = (object?)entry.TargetType ?? DBNull.Value });
                p.Add(new NpgsqlParameter("EntityId", NpgsqlDbType.Text) { Value = (object?)entry.TargetId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PerformedBy", NpgsqlDbType.Text) { Value = (object?)entry.ActorId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Details", (object?)metadataJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("OccurredAt", entry.OccurredAt));
                p.Add(new NpgsqlParameter("ImpersonatorId", NpgsqlDbType.Text) { Value = (object?)entry.ImpersonatorId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Category", NpgsqlDbType.Text) { Value = (object?)entry.Category ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Severity", NpgsqlDbType.Text) { Value = (object?)entry.Severity ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ActorType", NpgsqlDbType.Text) { Value = (object?)entry.ActorType ?? DBNull.Value });
                p.Add(new NpgsqlParameter("BeforeJson", (object?)beforeJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("AfterJson", (object?)afterJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("IntegrityHash", NpgsqlDbType.Text) { Value = (object?)entry.IntegrityHash ?? DBNull.Value });
                p.Add(new NpgsqlParameter("RetainUntil", NpgsqlDbType.TimestampTz) { Value = (object?)entry.RetainUntil?.UtcDateTime ?? DBNull.Value });
            },
            tx, ct);
    }

    // Mirrors PostgresAuditStore.SerializeChange exactly (including the AOT-diagnostic
    // suppressions and fallback path) — the correction endpoint's audit entry never actually sets
    // Changes today, but the writer stays faithful to the general IAuditService contract.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Audit-trail boundary — call sites pass arbitrary object?; serialization fallback documented in ADR-0006.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Audit-trail boundary — call sites pass arbitrary object?; serialization fallback documented in ADR-0006.")]
    private static string? SerializeChange(object? value)
    {
        if (value is null)
            return null;

        if (value is string s)
            return s;

        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), SerializeChangeOptions);
        }
        catch (NotSupportedException)
        {
            return JsonSerializer.Serialize(value.ToString(), PostgresJson.Ctx.String);
        }
        catch (InvalidOperationException)
        {
            return JsonSerializer.Serialize(value.ToString(), PostgresJson.Ctx.String);
        }
    }

    private static readonly JsonSerializerOptions SerializeChangeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
