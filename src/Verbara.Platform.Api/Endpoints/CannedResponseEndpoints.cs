using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class CannedResponseEndpoints
{
    internal sealed record CreateCannedResponseRequest(
        string Shortcut, string Title, string Body, string? Category, string[]? Tags);

    internal sealed record UpdateCannedResponseRequest(
        string? Shortcut, string? Title, string? Body, string? Category, string[]? Tags);

    internal sealed record CannedResponseDto(
        string ResponseId, string Shortcut, string Title, string Body,
        string? Category, IReadOnlyList<string> Tags, string CreatedBy, DateTimeOffset CreatedAt);

    internal static RouteGroupBuilder MapCannedResponseEndpoints(this RouteGroupBuilder group)
    {
        // Admin CRUD
        var admin = group.MapGroup("/admin/canned-responses")
            .WithTags("CannedResponses")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();

        admin.MapGet("/", ListAll);
        admin.MapPost("/", Create);
        admin.MapPut("/{id}", Update);
        admin.MapDelete("/{id}", Delete);

        // Agent search (authenticated)
        group.MapGet("/canned-responses", Search)
            .WithTags("CannedResponses")
            .RequireAuthorization("Authenticated");

        return group;
    }

    private static async Task<IResult> ListAll(
        HttpContext context,
        [FromServices] ICannedResponseStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListByTenantAsync(tenantId, ct);
        return Results.Ok(items.Select(ToDto).ToList());
    }

    private static async Task<IResult> Create(
        CreateCannedResponseRequest request,
        HttpContext context,
        [FromServices] ICannedResponseStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (string.IsNullOrWhiteSpace(request.Shortcut) ||
            string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Body))
            return Results.BadRequest(new ErrorResponse("Shortcut, Title, and Body are required"));

        // Check for duplicate shortcut
        var existing = await store.SearchAsync(tenantId, request.Shortcut, ct);
        if (existing.Any(r => r.Shortcut.Equals(request.Shortcut, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorResponse($"Shortcut '{request.Shortcut}' already exists"));

        var userId = context.User.FindFirst("sub")?.Value ?? "system";

        var response = new CannedResponse
        {
            ResponseId = EntityId.New(),
            TenantId = tenantId,
            Shortcut = request.Shortcut,
            Title = request.Title,
            Body = request.Body,
            Category = request.Category,
            Tags = request.Tags?.ToList() ?? [],
            CreatedBy = userId,
            CreatedAt = clock.UtcNow,
        };

        await store.SaveAsync(response, ct);
        return Results.Created($"/admin/canned-responses/{response.ResponseId.Value}", ToDto(response));
    }

    private static async Task<IResult> Update(
        string id,
        UpdateCannedResponseRequest request,
        HttpContext context,
        [FromServices] ICannedResponseStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        if (request.Shortcut is not null) existing.Shortcut = request.Shortcut;
        if (request.Title is not null) existing.Title = request.Title;
        if (request.Body is not null) existing.Body = request.Body;
        if (request.Category is not null) existing.Category = request.Category;
        if (request.Tags is not null) existing.Tags = request.Tags.ToList();
        existing.UpdatedAt = clock.UtcNow;

        await store.SaveAsync(existing, ct);
        return Results.Ok(ToDto(existing));
    }

    private static async Task<IResult> Delete(
        string id,
        HttpContext context,
        [FromServices] ICannedResponseStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Search(
        HttpContext context,
        [FromServices] ICannedResponseStore store,
        [FromQuery] string? q,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var items = string.IsNullOrWhiteSpace(q)
            ? await store.ListByTenantAsync(tenantId, ct)
            : await store.SearchAsync(tenantId, q, ct);

        return Results.Ok(items.Select(ToDto).ToList());
    }

    private static CannedResponseDto ToDto(CannedResponse r) =>
        new(r.ResponseId.Value, r.Shortcut, r.Title, r.Body,
            r.Category, r.Tags, r.CreatedBy, r.CreatedAt);

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
