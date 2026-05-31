using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Phase 2 voice-routing admin surface: CRUD for inbound DID → queue mappings.
/// Mirrors <see cref="TrunkEndpoints"/> structure but uses TEXT EntityId ids
/// (like queues/agents) rather than bigint. The DID is unique per tenant; a
/// duplicate create returns HTTP 409 Conflict.
/// </summary>
internal static class DidRouteEndpoints
{
    public static void MapDidRouteEndpoints(this IEndpointRouteBuilder app)
    {
        var dids = app.MapGroup("/admin/did-routes").RequireAuthorization("AdminOnly").RequireOperationalTenant();

        dids.MapGet("/", List);
        dids.MapGet("/active", ListActive);
        dids.MapGet("/by-did/{did}", GetByDid);
        dids.MapGet("/{id}", Get);
        dids.MapPost("/", Create);
        dids.MapPut("/{id}", Update);
        dids.MapDelete("/{id}", Delete);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<IResult> List(
        HttpContext context,
        [FromServices] IDidRouteStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(new TenantId(tenantId), ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> ListActive(
        HttpContext context,
        [FromServices] IDidRouteStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListActiveAsync(new TenantId(tenantId), ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetByDid(
        string did,
        HttpContext context,
        [FromServices] IDidRouteStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var route = await store.GetByDidAsync(new TenantId(tenantId), did, ct);
        return route is null ? Results.NotFound() : Results.Ok(MapToDto(route));
    }

    private static async Task<IResult> Get(
        string id,
        HttpContext context,
        [FromServices] IDidRouteStore store,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return Results.NotFound();

        var tenantId = GetTenantId(context);
        var route = await store.GetAsync(new TenantId(tenantId), EntityId.From(id), ct);
        return route is null ? Results.NotFound() : Results.Ok(MapToDto(route));
    }

    private static async Task<IResult> Create(
        HttpContext context,
        [FromBody] CreateDidRouteRequest body,
        [FromServices] IDidRouteStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (string.IsNullOrWhiteSpace(body.Did) || !EntityId.IsValid(body.QueueId))
            return Results.BadRequest();

        var route = new DidRoute
        {
            RouteId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            Did = body.Did,
            QueueId = EntityId.From(body.QueueId),
            IsActive = body.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        DidRoute created;
        try
        {
            created = await store.CreateAsync(route, ct);
        }
        catch (InvalidOperationException)
        {
            // UNIQUE (tenant_id, did) violation — duplicate DID for this tenant.
            return Results.Conflict();
        }

        var dto = MapToDto(created);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "didroute.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: created.RouteId.Value, targetType: "did_route",
            changes: new AuditChanges(Before: null, After: dto),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/api/v1/admin/did-routes/{created.RouteId.Value}", dto);
    }

    private static async Task<IResult> Update(
        string id,
        HttpContext context,
        [FromBody] UpdateDidRouteRequest body,
        [FromServices] IDidRouteStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return Results.NotFound();

        var tenantId = GetTenantId(context);
        var route = await store.GetAsync(new TenantId(tenantId), EntityId.From(id), ct);
        if (route is null)
            return Results.NotFound();

        var before = MapToDto(route);

        if (body.Did is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Did))
                return Results.BadRequest();
            route.Did = body.Did;
        }
        if (body.QueueId is not null)
        {
            if (!EntityId.IsValid(body.QueueId))
                return Results.BadRequest();
            route.QueueId = EntityId.From(body.QueueId);
        }
        if (body.IsActive.HasValue)
            route.IsActive = body.IsActive.Value;

        try
        {
            await store.UpdateAsync(route, ct);
        }
        catch (InvalidOperationException)
        {
            // Renaming onto an existing DID for this tenant — UNIQUE (tenant_id, did).
            return Results.Conflict();
        }

        var after = MapToDto(route);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "didroute.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: route.RouteId.Value, targetType: "did_route",
            changes: new AuditChanges(Before: before, After: after),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(after);
    }

    private static async Task<IResult> Delete(
        string id,
        HttpContext context,
        [FromServices] IDidRouteStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return Results.NotFound();

        var tenantId = GetTenantId(context);
        var routeId = EntityId.From(id);
        var before = await store.GetAsync(new TenantId(tenantId), routeId, ct);
        if (before is null)
            return Results.NotFound();

        await store.DeleteAsync(new TenantId(tenantId), routeId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "didroute.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: routeId.Value, targetType: "did_route",
            changes: new AuditChanges(Before: MapToDto(before), After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static DidRouteDto MapToDto(DidRoute r) =>
        new(r.RouteId.Value, r.Did, r.QueueId.Value, r.IsActive, r.CreatedAt, r.UpdatedAt);

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ─────────────────────────────────────────────────────

internal sealed record CreateDidRouteRequest(string Did, string QueueId, bool IsActive);

internal sealed record UpdateDidRouteRequest(string? Did, string? QueueId, bool? IsActive);

internal sealed record DidRouteDto(
    string Id, string Did, string QueueId, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);
