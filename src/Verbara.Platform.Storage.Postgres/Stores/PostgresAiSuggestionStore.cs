using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAiSuggestionStore : IAiSuggestionStore
{
    private const string SelectColumns =
        "id, tenant_id, conversation_id, schema_id, schema_version, " +
        "suggested_leaf_node_id, suggested_node_path, suggested_field_values, " +
        "confidence, sentiment, model_id, prompt_version, created_at, " +
        "committed_leaf_node_id, accepted";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresAiSuggestionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AiSuggestionRecord record, CancellationToken ct)
    {
        var nodePathJson = JsonSerializer.Serialize(
            record.SuggestedNodePath, PostgresJson.Ctx.IReadOnlyListString);
        var fieldValuesJson = JsonSerializer.Serialize(
            record.SuggestedFieldValues, PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO typification_ai_suggestions " +
            "(id, tenant_id, conversation_id, schema_id, schema_version, " +
            " suggested_leaf_node_id, suggested_node_path, suggested_field_values, " +
            " confidence, sentiment, model_id, prompt_version, created_at, " +
            " committed_leaf_node_id, accepted) " +
            "VALUES (@Id, @TenantId, @ConversationId, @SchemaId, @SchemaVersion, " +
            " @SuggestedLeafNodeId, @SuggestedNodePath::jsonb, @SuggestedFieldValues::jsonb, " +
            " @Confidence, @Sentiment, @ModelId, @PromptVersion, @CreatedAt, " +
            " @CommittedLeafNodeId, @Accepted)",
            p =>
            {
                p.Add(new NpgsqlParameter("Id", record.Id.Value));
                p.Add(new NpgsqlParameter("TenantId", record.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", record.ConversationId.Value));
                p.Add(new NpgsqlParameter("SchemaId", record.SchemaId.Value));
                p.Add(new NpgsqlParameter("SchemaVersion", record.SchemaVersion));
                p.Add(new NpgsqlParameter("SuggestedLeafNodeId", record.SuggestedLeafNodeId.Value));
                p.Add(new NpgsqlParameter("SuggestedNodePath", nodePathJson));
                p.Add(new NpgsqlParameter("SuggestedFieldValues", fieldValuesJson));
                p.Add(new NpgsqlParameter("Confidence", record.Confidence));
                p.Add(new NpgsqlParameter("Sentiment", NpgsqlDbType.Text)
                    { Value = (object?)record.Sentiment ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ModelId", record.ModelId));
                p.Add(new NpgsqlParameter("PromptVersion", record.PromptVersion));
                p.Add(new NpgsqlParameter("CreatedAt", record.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("CommittedLeafNodeId", NpgsqlDbType.Text)
                    { Value = (object?)record.CommittedLeafNodeId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Accepted", NpgsqlDbType.Boolean)
                    { Value = (object?)record.Accepted ?? DBNull.Value });
            },
            ct);
    }

    public async Task<AiSuggestionRecord?> GetLatestForConversationAsync(
        EntityId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM typification_ai_suggestions " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY created_at DESC",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
            },
            SuggestionRow.Map, ct);
        return row?.ToRecord();
    }

    public async Task MarkReconciledAsync(
        EntityId id, EntityId committedLeafNodeId, bool accepted, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE typification_ai_suggestions " +
            "SET committed_leaf_node_id = @CommittedLeafNodeId, accepted = @Accepted " +
            "WHERE id = @Id",
            p =>
            {
                p.Add(new NpgsqlParameter("Id", id.Value));
                p.Add(new NpgsqlParameter("CommittedLeafNodeId", committedLeafNodeId.Value));
                p.Add(new NpgsqlParameter("Accepted", accepted));
            },
            ct);
    }

    public async Task<(int Samples, double AcceptRate)> QueryAccuracyAsync(
        EntityId tenantId, EntityId schemaId, double confidenceThreshold, CancellationToken ct)
    {
        var total = await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM typification_ai_suggestions " +
            "WHERE tenant_id = @TenantId AND schema_id = @SchemaId " +
            "  AND accepted IS NOT NULL AND confidence >= @Threshold",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("SchemaId", schemaId.Value));
                p.Add(new NpgsqlParameter("Threshold", confidenceThreshold));
            },
            ct) ?? 0L;

        if (total == 0L)
            return (0, 0d);

        var accepted = await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM typification_ai_suggestions " +
            "WHERE tenant_id = @TenantId AND schema_id = @SchemaId " +
            "  AND accepted = true AND confidence >= @Threshold",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("SchemaId", schemaId.Value));
                p.Add(new NpgsqlParameter("Threshold", confidenceThreshold));
            },
            ct) ?? 0L;

        return ((int)total, (double)accepted / (double)total);
    }

    private sealed class SuggestionRow
    {
        public string id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string schema_id { get; init; } = null!;
        public int schema_version { get; init; }
        public string suggested_leaf_node_id { get; init; } = null!;
        public string suggested_node_path { get; init; } = null!;
        public string suggested_field_values { get; init; } = null!;
        public double confidence { get; init; }
        public string? sentiment { get; init; }
        public string model_id { get; init; } = null!;
        public string prompt_version { get; init; } = null!;
        public DateTime created_at { get; init; }
        public string? committed_leaf_node_id { get; init; }
        public bool? accepted { get; init; }

        public static SuggestionRow Map(NpgsqlDataReader r) => new()
        {
            id = r.GetString("id"),
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            schema_id = r.GetString("schema_id"),
            schema_version = r.GetInt32("schema_version"),
            suggested_leaf_node_id = r.GetString("suggested_leaf_node_id"),
            suggested_node_path = r.GetString("suggested_node_path"),
            suggested_field_values = r.GetString("suggested_field_values"),
            confidence = r.GetDouble("confidence"),
            sentiment = r.GetStringOrNull("sentiment"),
            model_id = r.GetString("model_id"),
            prompt_version = r.GetString("prompt_version"),
            created_at = r.GetDateTime("created_at"),
            committed_leaf_node_id = r.GetStringOrNull("committed_leaf_node_id"),
            accepted = r.GetBooleanOrNull("accepted"),
        };

        public AiSuggestionRecord ToRecord()
        {
            var nodePath = JsonSerializer.Deserialize(
                    suggested_node_path, PostgresJson.Ctx.IReadOnlyListString)
                ?? (IReadOnlyList<string>)[];
            var fieldValues = JsonSerializer.Deserialize(
                    suggested_field_values, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
                ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

            return new AiSuggestionRecord
            {
                Id = EntityId.From(id),
                TenantId = EntityId.From(tenant_id),
                ConversationId = EntityId.From(conversation_id),
                SchemaId = EntityId.From(schema_id),
                SchemaVersion = schema_version,
                SuggestedLeafNodeId = EntityId.From(suggested_leaf_node_id),
                SuggestedNodePath = nodePath,
                SuggestedFieldValues = fieldValues,
                Confidence = confidence,
                Sentiment = sentiment,
                ModelId = model_id,
                PromptVersion = prompt_version,
                CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
                CommittedLeafNodeId = committed_leaf_node_id is null ? null : EntityId.From(committed_leaf_node_id),
                Accepted = accepted,
            };
        }
    }
}
