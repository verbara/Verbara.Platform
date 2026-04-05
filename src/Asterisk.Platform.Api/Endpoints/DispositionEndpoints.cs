using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class DispositionEndpoints
{
    public static void MapDispositionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/dispositions").RequireAuthorization("AdminOnly");

        group.MapGet("/", ListDispositions);
        group.MapGet("/{id}", GetDisposition);
        group.MapPost("/", CreateDisposition);
        group.MapDelete("/{id}", DeleteDisposition);
    }

    private static async Task<IResult> ListDispositions(
        HttpContext context,
        [FromServices] IDispositionStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetDisposition(
        string id,
        HttpContext context,
        [FromServices] IDispositionStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var disposition = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return disposition is null ? Results.NotFound() : Results.Ok(disposition);
    }

    private static async Task<IResult> CreateDisposition(
        HttpContext context,
        [FromBody] CreateDispositionRequest body,
        [FromServices] IDispositionStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var disposition = new Disposition
        {
            DispositionId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            Category = body.Category,
            IsActive = true,
            CreatedAt = clock.UtcNow,
        };
        await store.SaveAsync(disposition, ct);
        return Results.Created($"/admin/dispositions/{disposition.DispositionId}", disposition);
    }

    private static async Task<IResult> DeleteDisposition(
        string id,
        HttpContext context,
        [FromServices] IDispositionStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateDispositionRequest(string Name, DispositionCategory Category);
