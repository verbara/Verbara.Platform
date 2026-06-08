using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTypificationSubmissionStore : ITypificationSubmissionStore
{
    private const string SelectColumns =
        "tenant_id, conversation_id, agent_id, schema_id, schema_version, selected_node_path, leaf_node_id, " +
        "field_values, notes, ai_suggested, ai_confidence, ai_accepted, source, duration_ms, completed_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTypificationSubmissionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(TypificationSubmission submission, CancellationToken ct)
    {
        IReadOnlyList<string> nodePath = submission.SelectedNodePath.Select(n => n.Value).ToList();
        var nodePathJson = JsonSerializer.Serialize(nodePath, PostgresJson.Ctx.IReadOnlyListString);
        var fieldValuesJson = JsonSerializer.Serialize(submission.FieldValues, PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO typification_submissions " +
            "(tenant_id, conversation_id, agent_id, schema_id, schema_version, selected_node_path, leaf_node_id, " +
            " field_values, notes, ai_suggested, ai_confidence, ai_accepted, source, duration_ms, completed_at) " +
            "VALUES (@TenantId, @ConversationId, @AgentId, @SchemaId, @SchemaVersion, @SelectedNodePath::jsonb, @LeafNodeId, " +
            " @FieldValues::jsonb, @Notes, @AiSuggested, @AiConfidence, @AiAccepted, @Source, @DurationMs, @CompletedAt) " +
            "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
            "  agent_id = EXCLUDED.agent_id, schema_id = EXCLUDED.schema_id, schema_version = EXCLUDED.schema_version, " +
            "  selected_node_path = EXCLUDED.selected_node_path, leaf_node_id = EXCLUDED.leaf_node_id, " +
            "  field_values = EXCLUDED.field_values, notes = EXCLUDED.notes, ai_suggested = EXCLUDED.ai_suggested, " +
            "  ai_confidence = EXCLUDED.ai_confidence, ai_accepted = EXCLUDED.ai_accepted, source = EXCLUDED.source, " +
            "  duration_ms = EXCLUDED.duration_ms, completed_at = EXCLUDED.completed_at",
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
            },
            ct);
    }

    public async Task<TypificationSubmission?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM typification_submissions " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
            },
            SubmissionRow.Map, ct);
        return row?.ToSubmission();
    }

    private sealed class SubmissionRow
    {
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string agent_id { get; init; } = null!;
        public string schema_id { get; init; } = null!;
        public int schema_version { get; init; }
        public string selected_node_path { get; init; } = null!;
        public string leaf_node_id { get; init; } = null!;
        public string field_values { get; init; } = null!;
        public string? notes { get; init; }
        public bool ai_suggested { get; init; }
        public double? ai_confidence { get; init; }
        public bool? ai_accepted { get; init; }
        public string source { get; init; } = null!;
        public long duration_ms { get; init; }
        public DateTime completed_at { get; init; }

        public static SubmissionRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            agent_id = r.GetString("agent_id"),
            schema_id = r.GetString("schema_id"),
            schema_version = r.GetInt32("schema_version"),
            selected_node_path = r.GetString("selected_node_path"),
            leaf_node_id = r.GetString("leaf_node_id"),
            field_values = r.GetString("field_values"),
            notes = r.GetStringOrNull("notes"),
            ai_suggested = r.GetBoolean("ai_suggested"),
            ai_confidence = r.IsDBNull(r.GetOrdinal("ai_confidence")) ? null : r.GetDouble("ai_confidence"),
            ai_accepted = r.GetBooleanOrNull("ai_accepted"),
            source = r.GetString("source"),
            duration_ms = r.GetInt64("duration_ms"),
            completed_at = r.GetDateTime("completed_at"),
        };

        public TypificationSubmission ToSubmission()
        {
            var path = JsonSerializer.Deserialize(selected_node_path, PostgresJson.Ctx.IReadOnlyListString)
                       ?? (IReadOnlyList<string>)[];
            var values = JsonSerializer.Deserialize(field_values, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
                         ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

            return new TypificationSubmission
            {
                TenantId = new TenantId(tenant_id),
                ConversationId = EntityId.From(conversation_id),
                AgentId = EntityId.From(agent_id),
                SchemaId = EntityId.From(schema_id),
                SchemaVersion = schema_version,
                SelectedNodePath = path.Select(EntityId.From).ToList(),
                LeafNodeId = EntityId.From(leaf_node_id),
                FieldValues = values,
                Notes = notes,
                AiSuggested = ai_suggested,
                AiConfidence = ai_confidence,
                AiAccepted = ai_accepted,
                Source = Enum.Parse<SubmissionSource>(source),
                Duration = TimeSpan.FromMilliseconds(duration_ms),
                CompletedAt = new DateTimeOffset(completed_at, TimeSpan.Zero),
            };
        }
    }
}
