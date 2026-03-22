using Asterisk.Platform.Core;
using Asterisk.Platform.KnowledgeBase;

namespace Asterisk.Platform.KnowledgeBase.Tests;

public class KeywordKnowledgeSearchTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static Article MakeArticle(
        TenantId? tenantId = null,
        string title = "Title",
        string content = "Content",
        bool isPublished = true) => new()
    {
        ArticleId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        Title = title,
        Content = content,
        IsPublished = isPublished,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static IArticleStore StoreReturning(params Article[] articles)
    {
        var store = Substitute.For<IArticleStore>();
        store
            .ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                new PagedResult<Article>(articles, articles.Length, 1, int.MaxValue)));
        return store;
    }

    [Fact]
    public async Task SearchAsync_ShouldFindMatchingArticle_WhenQueryMatchesContent()
    {
        var article = MakeArticle(title: "Billing FAQ", content: "You can pay your invoice online.");
        var search = new KeywordKnowledgeSearch(StoreReturning(article));

        var results = await search.SearchAsync(Tenant1, "invoice", 10, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Article.ArticleId.Should().Be(article.ArticleId);
    }

    [Fact]
    public async Task SearchAsync_ShouldScoreTitleMatchHigher_WhenTermAppearsInBothTitleAndContent()
    {
        var titleMatch = MakeArticle(title: "Password Reset", content: "Unrelated information here.");
        var contentMatch = MakeArticle(title: "Account Help", content: "How to reset your password easily.");
        var search = new KeywordKnowledgeSearch(StoreReturning(titleMatch, contentMatch));

        var results = await search.SearchAsync(Tenant1, "password reset", 10, CancellationToken.None);

        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results[0].Article.ArticleId.Should().Be(titleMatch.ArticleId);
        results[0].Score.Should().BeGreaterThan(results[1].Score);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenNoArticlesMatch()
    {
        var article = MakeArticle(title: "Shipping Policy", content: "We ship worldwide.");
        var search = new KeywordKnowledgeSearch(StoreReturning(article));

        var results = await search.SearchAsync(Tenant1, "refund", 10, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ShouldRespectMaxResults_WhenMoreMatchesExist()
    {
        var articles = Enumerable.Range(1, 5)
            .Select(i => MakeArticle(title: $"Article {i}", content: "common keyword here"))
            .ToArray();
        var search = new KeywordKnowledgeSearch(StoreReturning(articles));

        var results = await search.SearchAsync(Tenant1, "keyword", maxResults: 3, CancellationToken.None);

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchAsync_ShouldExcludeUnpublishedArticles_WhenIsPublishedIsFalse()
    {
        var published = MakeArticle(title: "Help Article", content: "This article helps users.", isPublished: true);
        var unpublished = MakeArticle(title: "Hidden Article", content: "This article is hidden.", isPublished: false);
        var search = new KeywordKnowledgeSearch(StoreReturning(published, unpublished));

        var results = await search.SearchAsync(Tenant1, "article", 10, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Article.ArticleId.Should().Be(published.ArticleId);
    }

    [Fact]
    public async Task SearchAsync_ShouldMatchAllQueryWords_WhenMultiWordQuery()
    {
        var both = MakeArticle(title: "Password and Account", content: "Reset your password and manage your account.");
        var oneWord = MakeArticle(title: "Password Only", content: "Change your password here.");
        var search = new KeywordKnowledgeSearch(StoreReturning(both, oneWord));

        var results = await search.SearchAsync(Tenant1, "password account", 10, CancellationToken.None);

        results.Should().HaveCount(2);
        // 'both' article matches all query words so should rank first
        results[0].Article.ArticleId.Should().Be(both.ArticleId);
    }

    [Fact]
    public async Task SearchAsync_ShouldBeCaseInsensitive_WhenQueryHasDifferentCase()
    {
        var article = MakeArticle(title: "Invoice Payment", content: "Pay your INVOICE online.");
        var search = new KeywordKnowledgeSearch(StoreReturning(article));

        var results = await search.SearchAsync(Tenant1, "invoice", 10, CancellationToken.None);

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchAsync_ShouldIsolateTenants_WhenArticlesBelongToDifferentTenants()
    {
        var tenant1Article = MakeArticle(tenantId: Tenant1, title: "Help Topic", content: "Useful guide for users.");
        var tenant2Article = MakeArticle(tenantId: Tenant2, title: "Help Topic", content: "Useful guide for users.");

        // Store returns only tenant-1 articles when queried with Tenant1
        var store = Substitute.For<IArticleStore>();
        store
            .ListAsync(Tenant1, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Article>([tenant1Article], 1, 1, int.MaxValue)));
        store
            .ListAsync(Tenant2, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Article>([tenant2Article], 1, 1, int.MaxValue)));

        var search = new KeywordKnowledgeSearch(store);

        var tenant1Results = await search.SearchAsync(Tenant1, "guide", 10, CancellationToken.None);
        var tenant2Results = await search.SearchAsync(Tenant2, "guide", 10, CancellationToken.None);

        tenant1Results.Should().HaveCount(1);
        tenant1Results[0].Article.TenantId.Should().Be(Tenant1);

        tenant2Results.Should().HaveCount(1);
        tenant2Results[0].Article.TenantId.Should().Be(Tenant2);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnResultsOrderedByScoreDescending_WhenMultipleMatches()
    {
        // strong: matches in both title and content → high score
        var strong = MakeArticle(title: "Refund Policy", content: "Our refund policy covers all products.");
        // weak: matches only in content → lower score
        var weak = MakeArticle(title: "Shipping Info", content: "Request a refund within 7 days.");
        var search = new KeywordKnowledgeSearch(StoreReturning(weak, strong)); // reversed insertion order

        var results = await search.SearchAsync(Tenant1, "refund", 10, CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Score.Should().BeGreaterThanOrEqualTo(results[1].Score);
        results[0].Article.ArticleId.Should().Be(strong.ArticleId);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenStoreHasNoArticles()
    {
        var search = new KeywordKnowledgeSearch(StoreReturning());

        var results = await search.SearchAsync(Tenant1, "anything", 10, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public void SearchResult_ShouldExposeArticleAndScore()
    {
        var article = MakeArticle();
        var result = new SearchResult(article, 0.75);

        result.Article.Should().BeSameAs(article);
        result.Score.Should().Be(0.75);
    }
}
