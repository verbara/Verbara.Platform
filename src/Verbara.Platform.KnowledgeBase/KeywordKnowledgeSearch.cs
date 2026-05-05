using Verbara.Platform.Core;

namespace Verbara.Platform.KnowledgeBase;

/// <summary>
/// Default keyword-based implementation of <see cref="IKnowledgeSearch"/>.
/// Scores articles using a simple TF-IDF-like heuristic:
/// title token matches are weighted 2x, content token matches are weighted 1x.
/// Only published articles are considered.
/// This implementation is stateless and thread-safe.
/// </summary>
public sealed class KeywordKnowledgeSearch : IKnowledgeSearch
{
    private const int TitleWeight = 2;
    private const int ContentWeight = 1;

    private readonly IArticleStore _store;

    /// <summary>Initialises a new instance with the given article store.</summary>
    public KeywordKnowledgeSearch(IArticleStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        TenantId tenantId,
        string query,
        int maxResults,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var tokens = Tokenize(query);
        if (tokens.Length == 0)
            return [];

        // Fetch all articles — paging is done in a single large page for in-memory scoring.
        var page = await _store.ListAsync(tenantId, new PagedQuery(Page: 1, PageSize: int.MaxValue), ct)
            .ConfigureAwait(false);

        var results = new List<SearchResult>(page.Items.Count);

        foreach (var article in page.Items)
        {
            if (!article.IsPublished)
                continue;

            var score = Score(article, tokens);
            if (score > 0d)
                results.Add(new SearchResult(article, score));
        }

        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        return results.Count <= maxResults
            ? results.AsReadOnly()
            : results.GetRange(0, maxResults).AsReadOnly();
    }

    private static double Score(Article article, string[] queryTokens)
    {
        var titleTokens = Tokenize(article.Title);
        var contentTokens = Tokenize(article.Content);

        var totalWeight = queryTokens.Length * (TitleWeight + ContentWeight);
        if (totalWeight == 0)
            return 0d;

        var matchWeight = 0;
        foreach (var token in queryTokens)
        {
            if (ContainsToken(titleTokens, token))
                matchWeight += TitleWeight;
            if (ContainsToken(contentTokens, token))
                matchWeight += ContentWeight;
        }

        return (double)matchWeight / totalWeight;
    }

    private static bool ContainsToken(string[] tokens, string target)
    {
        foreach (var t in tokens)
        {
            if (string.Equals(t, target, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string[] Tokenize(string text) =>
        text.Split([' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
