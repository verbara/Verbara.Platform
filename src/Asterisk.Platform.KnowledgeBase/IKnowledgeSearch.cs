using Asterisk.Platform.Core;

namespace Asterisk.Platform.KnowledgeBase;

/// <summary>
/// Searches the knowledge base for articles relevant to a natural-language query.
/// </summary>
public interface IKnowledgeSearch
{
    /// <summary>
    /// Returns up to <paramref name="maxResults"/> articles ranked by relevance to <paramref name="query"/>
    /// within the given tenant's knowledge base.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        TenantId tenantId,
        string query,
        int maxResults,
        CancellationToken ct);
}
