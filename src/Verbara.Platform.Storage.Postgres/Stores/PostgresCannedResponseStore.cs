using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresCannedResponseStore : ICannedResponseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCannedResponseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "response_id, tenant_id, shortcut, title, body, category, tags, created_by, created_at, updated_at";

    public async Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM canned_responses WHERE tenant_id = @TenantId AND response_id = @ResponseId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ResponseId", responseId.Value)); },
            CannedResponseRow.Map, ct);

        return row?.ToModel();
    }

    public async Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM canned_responses WHERE tenant_id = @TenantId ORDER BY shortcut",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); },
            CannedResponseRow.Map, ct);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct)
    {
        var pattern = $"%{query}%";
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM canned_responses " +
            "WHERE tenant_id = @TenantId AND (" +
            "  shortcut ILIKE @Pattern OR title ILIKE @Pattern OR body ILIKE @Pattern " +
            "  OR category ILIKE @Pattern OR tags ILIKE @Pattern" +
            ") ORDER BY shortcut",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Pattern", pattern)); },
            CannedResponseRow.Map, ct);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(CannedResponse response, CancellationToken ct)
    {
        var tagsJson = response.Tags.Count > 0
            ? PostgresJson.Serialize(response.Tags.ToList(), PostgresJson.Ctx.ListString)
            : null;

        await _dataSource.ExecuteAsync(
            "INSERT INTO canned_responses " +
            "(response_id, tenant_id, shortcut, title, body, category, tags, created_by, created_at, updated_at) " +
            "VALUES (@ResponseId, @TenantId, @Shortcut, @Title, @Body, @Category, @Tags, @CreatedBy, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, response_id) DO UPDATE SET " +
            "  shortcut   = EXCLUDED.shortcut, " +
            "  title      = EXCLUDED.title, " +
            "  body       = EXCLUDED.body, " +
            "  category   = EXCLUDED.category, " +
            "  tags       = EXCLUDED.tags, " +
            "  updated_at = NOW()",
            p =>
            {
                p.Add(new NpgsqlParameter("ResponseId", response.ResponseId.Value));
                p.Add(new NpgsqlParameter("TenantId", response.TenantId.Value));
                p.Add(new NpgsqlParameter("Shortcut", response.Shortcut));
                p.Add(new NpgsqlParameter("Title", response.Title));
                p.Add(new NpgsqlParameter("Body", response.Body));
                p.Add(new NpgsqlParameter("Category", NpgsqlDbType.Text) { Value = (object?)response.Category ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Tags", NpgsqlDbType.Text) { Value = (object?)tagsJson ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedBy", response.CreatedBy));
                p.Add(new NpgsqlParameter("CreatedAt", response.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)response.UpdatedAt?.UtcDateTime ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM canned_responses WHERE tenant_id = @TenantId AND response_id = @ResponseId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ResponseId", responseId.Value)); },
            ct);
    }

    private sealed class CannedResponseRow
    {
        public string response_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string shortcut { get; init; } = null!;
        public string title { get; init; } = null!;
        public string body { get; init; } = null!;
        public string? category { get; init; }
        public string? tags { get; init; }
        public string created_by { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }

        public static CannedResponseRow Map(NpgsqlDataReader r) => new()
        {
            response_id = r.GetString("response_id"),
            tenant_id = r.GetString("tenant_id"),
            shortcut = r.GetString("shortcut"),
            title = r.GetString("title"),
            body = r.GetString("body"),
            category = r.GetStringOrNull("category"),
            tags = r.GetStringOrNull("tags"),
            created_by = r.GetString("created_by"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
        };

        public CannedResponse ToModel()
        {
            var tagsList = !string.IsNullOrEmpty(tags)
                ? PostgresJson.Deserialize(tags, PostgresJson.Ctx.ListString) ?? []
                : new List<string>();

            return new CannedResponse
            {
                ResponseId = EntityId.From(response_id),
                TenantId = new TenantId(tenant_id),
                Shortcut = shortcut,
                Title = title,
                Body = body,
                Category = category,
                Tags = tagsList,
                CreatedBy = created_by,
                CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
                UpdatedAt = updated_at.HasValue ? new DateTimeOffset(updated_at.Value, TimeSpan.Zero) : null,
            };
        }
    }
}
