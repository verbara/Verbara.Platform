using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.KnowledgeBase;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresArticleStore : IArticleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresArticleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Article?> GetByIdAsync(TenantId tenantId, EntityId articleId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT article_id, tenant_id, title, content, tags, language, is_published, created_at, updated_at " +
            "FROM articles WHERE tenant_id = @TenantId AND article_id = @ArticleId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ArticleId", articleId.Value)); },
            ArticleRow.Map, ct);
        return row?.ToArticle();
    }

    public async Task<PagedResult<Article>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM articles WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);
        var rows = await _dataSource.QueryListAsync(
            "SELECT article_id, tenant_id, title, content, tags, language, is_published, created_at, updated_at " +
            "FROM articles WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
            ArticleRow.Map, ct);
        var items = rows.Select(r => r.ToArticle()).ToList();
        return new PagedResult<Article>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Article article, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO articles (article_id, tenant_id, title, content, tags, language, is_published, created_at, updated_at) " +
            "VALUES (@ArticleId, @TenantId, @Title, @Content, @Tags, @Language, @IsPublished, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, article_id) DO UPDATE SET " +
            "  title = EXCLUDED.title, content = EXCLUDED.content, tags = EXCLUDED.tags, " +
            "  language = EXCLUDED.language, is_published = EXCLUDED.is_published, updated_at = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("ArticleId", article.ArticleId.Value));
                p.Add(new NpgsqlParameter("TenantId", article.TenantId.Value));
                p.Add(new NpgsqlParameter("Title", article.Title));
                p.Add(new NpgsqlParameter("Content", article.Content));
                p.Add(new NpgsqlParameter("Tags", article.Tags.ToArray()));
                p.Add(new NpgsqlParameter("Language", (object?)article.Language ?? DBNull.Value));
                p.Add(new NpgsqlParameter("IsPublished", article.IsPublished));
                p.Add(new NpgsqlParameter("CreatedAt", article.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", (object?)article.UpdatedAt ?? DBNull.Value));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId articleId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM articles WHERE tenant_id = @TenantId AND article_id = @ArticleId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ArticleId", articleId.Value)); },
            ct);
    }

    private sealed class ArticleRow
    {
        public string article_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string title { get; init; } = null!;
        public string content { get; init; } = null!;
        public string[] tags { get; init; } = null!;
        public string? language { get; init; }
        public bool is_published { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }

        public static ArticleRow Map(NpgsqlDataReader r) => new()
        {
            article_id = r.GetString("article_id"),
            tenant_id = r.GetString("tenant_id"),
            title = r.GetString("title"),
            content = r.GetString("content"),
            tags = r.GetFieldValue<string[]>(r.GetOrdinal("tags")),
            language = r.GetStringOrNull("language"),
            is_published = r.GetBoolean("is_published"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
        };

        public Article ToArticle() => new()
        {
            ArticleId = EntityId.From(article_id),
            TenantId = new TenantId(tenant_id),
            Title = title,
            Content = content,
            Tags = tags,
            Language = language,
            IsPublished = is_published,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }
}
