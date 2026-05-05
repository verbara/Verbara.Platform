using Dapper;
using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresCannedResponseStore : ICannedResponseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCannedResponseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "response_id, tenant_id, shortcut, title, body, category, tags, created_by, created_at, updated_at";

    public async Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CannedResponseRow>(
            $"SELECT {SelectColumns} FROM canned_responses WHERE tenant_id = @TenantId AND response_id = @ResponseId",
            new { TenantId = tenantId.Value, ResponseId = responseId.Value });

        return row?.ToModel();
    }

    public async Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CannedResponseRow>(
            $"SELECT {SelectColumns} FROM canned_responses WHERE tenant_id = @TenantId ORDER BY shortcut",
            new { TenantId = tenantId.Value });

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var pattern = $"%{query}%";
        var rows = await conn.QueryAsync<CannedResponseRow>(
            $"SELECT {SelectColumns} FROM canned_responses " +
            "WHERE tenant_id = @TenantId AND (" +
            "  shortcut ILIKE @Pattern OR title ILIKE @Pattern OR body ILIKE @Pattern " +
            "  OR category ILIKE @Pattern OR tags ILIKE @Pattern" +
            ") ORDER BY shortcut",
            new { TenantId = tenantId.Value, Pattern = pattern });

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(CannedResponse response, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var tagsJson = response.Tags.Count > 0
            ? PostgresJson.Serialize(response.Tags.ToList(), PostgresJson.Ctx.ListString)
            : null;

        await conn.ExecuteAsync(
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
            new
            {
                ResponseId = response.ResponseId.Value,
                TenantId = response.TenantId.Value,
                Shortcut = response.Shortcut,
                Title = response.Title,
                Body = response.Body,
                Category = response.Category,
                Tags = tagsJson,
                CreatedBy = response.CreatedBy,
                CreatedAt = response.CreatedAt.UtcDateTime,
                UpdatedAt = response.UpdatedAt?.UtcDateTime,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM canned_responses WHERE tenant_id = @TenantId AND response_id = @ResponseId",
            new { TenantId = tenantId.Value, ResponseId = responseId.Value });
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
