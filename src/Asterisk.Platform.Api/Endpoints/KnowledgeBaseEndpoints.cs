using Asterisk.Platform.Core;
using Asterisk.Platform.KnowledgeBase;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class KnowledgeBaseEndpoints
{
    public static void MapKnowledgeBaseEndpoints(this IEndpointRouteBuilder app)
    {
        var adminGroup = app.MapGroup("/admin/articles")
            .RequireAuthorization("AdminOnly")
            .RequirePlanFeature(PlanFeature.KnowledgeBase);

        adminGroup.MapGet("/", ListArticles);
        adminGroup.MapPost("/", CreateArticle);
        adminGroup.MapPut("/{id}", UpdateArticle);
        adminGroup.MapDelete("/{id}", DeleteArticle);

        var searchGroup = app.MapGroup("/knowledge")
            .RequireAuthorization()
            .RequirePlanFeature(PlanFeature.KnowledgeBase);

        searchGroup.MapGet("/search", SearchArticles);
    }

    // ─── Admin CRUD ───────────────────────────────────────────────────────────

    private static async Task<IResult> ListArticles(
        HttpContext context,
        [FromServices] IArticleStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, ct);
        var items = result.Items.Select(ToArticleDto).ToArray();
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateArticle(
        HttpContext context,
        [FromBody] CreateArticleRequest body,
        [FromServices] IArticleStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var article = new Article
        {
            ArticleId = EntityId.New(),
            TenantId = tenantId,
            Title = body.Title,
            Content = body.Content,
            Tags = body.Tags ?? [],
            IsPublished = body.IsPublished ?? true,
            Language = body.Language,
            CreatedAt = clock.UtcNow,
        };
        await store.SaveAsync(article, ct);
        return Results.Created($"/admin/articles/{article.ArticleId}", ToArticleDto(article));
    }

    private static async Task<IResult> UpdateArticle(
        string id,
        HttpContext context,
        [FromBody] UpdateArticleRequest body,
        [FromServices] IArticleStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var article = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (article is null)
            return Results.NotFound();

        if (body.Title is not null) article.Title = body.Title;
        if (body.Content is not null) article.Content = body.Content;
        if (body.Tags is not null) article.Tags = body.Tags;
        if (body.IsPublished.HasValue) article.IsPublished = body.IsPublished.Value;
        if (body.Language is not null) article.Language = body.Language;
        article.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(article, ct);
        return Results.Ok(ToArticleDto(article));
    }

    private static ArticleDto ToArticleDto(Article a) =>
        new(
            Id: a.ArticleId.Value,
            Title: a.Title,
            Content: a.Content,
            Tags: a.Tags,
            Published: a.IsPublished,
            Language: a.Language,
            CreatedAt: a.CreatedAt,
            UpdatedAt: a.UpdatedAt ?? a.CreatedAt);

    private static async Task<IResult> DeleteArticle(
        string id,
        HttpContext context,
        [FromServices] IArticleStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ─── Agent Search ─────────────────────────────────────────────────────────

    private static async Task<IResult> SearchArticles(
        HttpContext context,
        IKnowledgeSearch search,
        string q,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest("Query parameter 'q' is required.");

        var tenantId = GetTenantId(context);
        var results = await search.SearchAsync(tenantId, q, limit, ct);
        return Results.Ok(results);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateArticleRequest(
    string Title,
    string Content,
    IReadOnlyList<string>? Tags = null,
    bool? IsPublished = null,
    string? Language = null);

internal sealed record UpdateArticleRequest(
    string? Title = null,
    string? Content = null,
    IReadOnlyList<string>? Tags = null,
    bool? IsPublished = null,
    string? Language = null);

internal sealed record ArticleDto(
    string Id,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    bool Published,
    string? Language,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
