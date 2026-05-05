using Dapper;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.KnowledgeBase;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresArticleStore : IArticleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresArticleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Article?> GetByIdAsync(TenantId tenantId, EntityId articleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ArticleRow>(
            "SELECT article_id, tenant_id, title, content, tags, language, is_published, created_at, updated_at " +
            "FROM articles WHERE tenant_id = @TenantId AND article_id = @ArticleId",
            new { TenantId = tenantId.Value, ArticleId = articleId.Value });
        return row?.ToArticle();
    }

    public async Task<PagedResult<Article>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var parameters = new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset };

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM articles WHERE tenant_id = @TenantId",
            parameters);
        var rows = await conn.QueryAsync<ArticleRow>(
            "SELECT article_id, tenant_id, title, content, tags, language, is_published, created_at, updated_at " +
            "FROM articles WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            parameters);
        var items = rows.Select(r => r.ToArticle()).ToList();
        return new PagedResult<Article>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Article article, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO articles (article_id, tenant_id, title, content, tags, language, is_published, created_at, updated_at) " +
            "VALUES (@ArticleId, @TenantId, @Title, @Content, @Tags, @Language, @IsPublished, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, article_id) DO UPDATE SET " +
            "  title = EXCLUDED.title, content = EXCLUDED.content, tags = EXCLUDED.tags, " +
            "  language = EXCLUDED.language, is_published = EXCLUDED.is_published, updated_at = EXCLUDED.updated_at",
            new
            {
                ArticleId = article.ArticleId.Value,
                TenantId = article.TenantId.Value,
                article.Title,
                article.Content,
                Tags = article.Tags.ToArray(),
                article.Language,
                article.IsPublished,
                article.CreatedAt,
                article.UpdatedAt,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId articleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM articles WHERE tenant_id = @TenantId AND article_id = @ArticleId",
            new { TenantId = tenantId.Value, ArticleId = articleId.Value });
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
