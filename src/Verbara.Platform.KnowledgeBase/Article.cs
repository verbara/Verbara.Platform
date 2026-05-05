using Verbara.Platform.Core;

namespace Verbara.Platform.KnowledgeBase;

/// <summary>
/// Represents a knowledge base article scoped to a tenant.
/// </summary>
public sealed class Article : ITenantScoped
{
    /// <summary>The unique identifier for this article.</summary>
    public required EntityId ArticleId { get; init; }

    /// <summary>The tenant this article belongs to.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The article title.</summary>
    public required string Title { get; set; }

    /// <summary>The full article content.</summary>
    public required string Content { get; set; }

    /// <summary>Optional tags used for categorisation and filtering.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>When <see langword="true"/> the article is visible to search; defaults to <see langword="true"/>.</summary>
    public bool IsPublished { get; set; } = true;

    /// <summary>BCP-47 language tag (e.g. "en", "es"); <see langword="null"/> means unspecified.</summary>
    public string? Language { get; set; }

    /// <summary>When the article was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the article was last modified, or <see langword="null"/> if never updated.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
