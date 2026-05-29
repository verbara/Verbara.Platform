using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class DispositionEndpoints
{
    public static void MapDispositionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/dispositions")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Dialer);

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
        [FromServices] IAuditService audit,
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
        await audit.RecordAsync(
            tenantId, category: "config", action: "disposition.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: disposition.DispositionId.Value, targetType: "disposition",
            changes: new AuditChanges(Before: null, After: new { disposition.DispositionId, disposition.Name, disposition.Category }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/dispositions/{disposition.DispositionId}", disposition);
    }

    private static async Task<IResult> DeleteDisposition(
        string id,
        HttpContext context,
        [FromServices] IDispositionStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var before = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "disposition.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "disposition",
            changes: new AuditChanges(Before: before is null ? null : new { before.DispositionId, before.Name }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
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
