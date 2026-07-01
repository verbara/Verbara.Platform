using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed append-only store for supervisor corrections of autonomously stamped
/// dispositions (ADR-0034 Decision 5). One row per conversation in
/// <c>typification_submission_corrections</c>; the original <c>typification_submissions</c> row is
/// never touched here. <c>corrected_node_path</c> is a JSONB string array (the sanctioned <c>::jsonb</c>
/// cast), every param bound with an explicit <see cref="NpgsqlDbType"/>.
/// </summary>
internal sealed class PostgresTypificationSubmissionCorrectionStore : ITypificationSubmissionCorrectionStore
{
    private const string SelectColumns =
        "tenant_id, conversation_id, corrected_leaf_node_id, corrected_node_path, corrected_by_user_id, corrected_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTypificationSubmissionCorrectionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TypificationSubmissionCorrection?> GetAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM typification_submission_corrections " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
            },
            Row.Map, ct);
        return row?.ToRecord();
    }

    public async Task InsertAsync(TypificationSubmissionCorrection record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        IReadOnlyList<string> nodePath = record.CorrectedNodePath.Select(n => n.Value).ToList();
        var nodePathJson = JsonSerializer.Serialize(nodePath, PostgresJson.Ctx.IReadOnlyListString);

        await _dataSource.ExecuteAsync(
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
            ct);
    }

    private sealed class Row
    {
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string corrected_leaf_node_id { get; init; } = null!;
        public string corrected_node_path { get; init; } = null!;
        public string corrected_by_user_id { get; init; } = null!;
        public DateTime corrected_at { get; init; }

        public static Row Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            corrected_leaf_node_id = r.GetString("corrected_leaf_node_id"),
            corrected_node_path = r.GetString("corrected_node_path"),
            corrected_by_user_id = r.GetString("corrected_by_user_id"),
            corrected_at = r.GetDateTime("corrected_at"),
        };

        public TypificationSubmissionCorrection ToRecord()
        {
            var path = JsonSerializer.Deserialize(corrected_node_path, PostgresJson.Ctx.IReadOnlyListString)
                       ?? (IReadOnlyList<string>)[];

            return new TypificationSubmissionCorrection
            {
                TenantId = new TenantId(tenant_id),
                ConversationId = EntityId.From(conversation_id),
                CorrectedLeafNodeId = EntityId.From(corrected_leaf_node_id),
                CorrectedNodePath = path.Select(EntityId.From).ToList(),
                CorrectedByUserId = corrected_by_user_id,
                CorrectedAt = new DateTimeOffset(corrected_at, TimeSpan.Zero),
            };
        }
    }
}
