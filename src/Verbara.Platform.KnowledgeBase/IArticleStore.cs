using Verbara.Platform.Core;

namespace Verbara.Platform.KnowledgeBase;

/// <summary>
/// Persistence contract for knowledge-base articles.
/// </summary>
public interface IArticleStore
{
    /// <summary>Returns the article with <paramref name="articleId"/> for the given tenant, or <see langword="null"/> if not found.</summary>
    Task<Article?> GetByIdAsync(TenantId tenantId, EntityId articleId, CancellationToken ct);

    /// <summary>Returns a paged list of articles for the given tenant.</summary>
    Task<PagedResult<Article>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct);

    /// <summary>Creates or updates the article.</summary>
    Task SaveAsync(Article article, CancellationToken ct);

    /// <summary>Permanently removes the article identified by <paramref name="articleId"/> for the given tenant.</summary>
    Task DeleteAsync(TenantId tenantId, EntityId articleId, CancellationToken ct);
}
