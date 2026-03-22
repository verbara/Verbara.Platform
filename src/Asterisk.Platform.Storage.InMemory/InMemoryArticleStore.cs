using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.KnowledgeBase;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryArticleStore : IArticleStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Article> _items = new();

    public Task<Article?> GetByIdAsync(TenantId tenantId, EntityId articleId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, articleId), out var item);
        return Task.FromResult(item);
    }

    public Task<PagedResult<Article>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(a => a.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Article>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Article article, CancellationToken ct)
    {
        _items[(article.TenantId, article.ArticleId)] = article;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId articleId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, articleId), out _);
        return Task.CompletedTask;
    }
}
