using Verbara.Platform.Core;
using Verbara.Platform.KnowledgeBase;

namespace Verbara.Platform.KnowledgeBase.Tests;

public class ArticleTests
{
    private static Article MakeArticle(
        string title = "Password Reset Guide",
        string content = "To reset your password visit the login page.",
        bool isPublished = true) => new()
    {
        ArticleId = EntityId.New(),
        TenantId = new TenantId("tenant-1"),
        Title = title,
        Content = content,
        IsPublished = isPublished,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Article_ShouldHaveCorrectTenantId_WhenCreated()
    {
        var article = MakeArticle();
        article.TenantId.Value.Should().Be("tenant-1");
    }

    [Fact]
    public void Article_ShouldDefaultIsPublishedToTrue_WhenCreated()
    {
        var article = MakeArticle();
        article.IsPublished.Should().BeTrue();
    }

    [Fact]
    public void Article_ShouldDefaultTagsToEmpty_WhenCreated()
    {
        var article = MakeArticle();
        article.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Article_ShouldAllowSettingTags_WhenProvided()
    {
        var article = MakeArticle();
        article.Tags = ["faq", "password"];
        article.Tags.Should().Equal("faq", "password");
    }

    [Fact]
    public void Article_ShouldAllowLanguage_WhenSet()
    {
        var article = MakeArticle();
        article.Language = "en";
        article.Language.Should().Be("en");
    }

    [Fact]
    public void Article_ShouldHaveNullLanguage_ByDefault()
    {
        var article = MakeArticle();
        article.Language.Should().BeNull();
    }

    [Fact]
    public void Article_ShouldAllowUpdatedAt_WhenModified()
    {
        var article = MakeArticle();
        var now = DateTimeOffset.UtcNow;
        article.UpdatedAt = now;
        article.UpdatedAt.Should().Be(now);
    }

    [Fact]
    public void Article_ShouldImplementITenantScoped()
    {
        var article = MakeArticle();
        article.Should().BeAssignableTo<Verbara.Platform.Core.ITenantScoped>();
    }
}
