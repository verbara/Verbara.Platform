using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class OutboundRouteEndpoints
{
    public static void MapOutboundRouteEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/admin/routes").RequireAuthorization("AdminOnly").RequireOperationalTenant();

        routes.MapGet("/", ListRoutes);
        routes.MapGet("/{id:long}", GetRoute);
        routes.MapPost("/", CreateRoute);
        routes.MapPut("/{id:long}", UpdateRoute);
        routes.MapDelete("/{id:long}", DeleteRoute);
        routes.MapPut("/reorder", ReorderRoutes);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<Ok<List<OutboundRouteDto>>> ListRoutes(
        HttpContext context,
        [FromServices] OutboundRouteStoreBase routeStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await routeStore.ListByPriorityAsync(tenantId, ct);
        return TypedResults.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<Results<Ok<OutboundRouteDto>, NotFound>> GetRoute(
        long id,
        HttpContext context,
        [FromServices] OutboundRouteStoreBase routeStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var route = await routeStore.GetAsync(id, tenantId, ct);
        return route is null ? TypedResults.NotFound() : TypedResults.Ok(MapToDto(route));
    }

    private static async Task<Results<Created<OutboundRouteDto>, BadRequest<ErrorResponse>>> CreateRoute(
        HttpContext context,
        [FromBody] CreateOutboundRouteRequest body,
        OutboundRouteStoreBase routeStore,
        [FromServices] TrunkStoreBase trunkStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var refusal = await RefuseTrunksOutsideTenantAsync(trunkStore, body.TrunkId, body.OverflowTrunkId, tenantId, ct);
        if (refusal is not null)
            return refusal;

        var route = new OutboundRoute
        {
            CampaignId = body.CampaignId,
            Pattern = body.Pattern,
            PatternType = ParsePatternType(body.PatternType),
            TrunkId = body.TrunkId,
            OverflowTrunkId = body.OverflowTrunkId,
            DialPrefix = body.DialPrefix,
            Priority = body.Priority,
        };
        var id = await routeStore.CreateAsync(route, tenantId, ct);
        route.Id = id;
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "outbound_route.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "outbound_route",
            changes: new AuditChanges(Before: null, After: MapToDto(route)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return TypedResults.Created($"/admin/routes/{id}", MapToDto(route));
    }

    private static async Task<Results<Ok<OutboundRouteDto>, NotFound, BadRequest<ErrorResponse>>> UpdateRoute(
        long id,
        HttpContext context,
        [FromBody] UpdateOutboundRouteRequest body,
        OutboundRouteStoreBase routeStore,
        [FromServices] TrunkStoreBase trunkStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var route = await routeStore.GetAsync(id, tenantId, ct);
        if (route is null)
            return TypedResults.NotFound();

        // Only a trunk id this request supplies and that differs from the route's stored value is checked (design D2):
        // a value sent back unchanged adds no trunk reference, so a client that resends the whole route keeps
        // working when its overflow trunk was since deleted (overflow_trunk_id has no foreign key).
        var refusal = await RefuseTrunksOutsideTenantAsync(
            trunkStore,
            body.TrunkId != route.TrunkId ? body.TrunkId : null,
            body.OverflowTrunkId != route.OverflowTrunkId ? body.OverflowTrunkId : null,
            tenantId,
            ct);
        if (refusal is not null)
            return refusal;

        var before = MapToDto(route);

        if (body.CampaignId.HasValue) route.CampaignId = body.CampaignId;
        if (body.Pattern is not null) route.Pattern = body.Pattern;
        if (body.PatternType is not null) route.PatternType = ParsePatternType(body.PatternType);
        if (body.TrunkId.HasValue) route.TrunkId = body.TrunkId.Value;
        if (body.OverflowTrunkId.HasValue) route.OverflowTrunkId = body.OverflowTrunkId;
        if (body.DialPrefix is not null) route.DialPrefix = body.DialPrefix;
        if (body.Priority.HasValue) route.Priority = body.Priority.Value;

        await routeStore.UpdateAsync(route, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "outbound_route.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "outbound_route",
            changes: new AuditChanges(Before: before, After: MapToDto(route)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return TypedResults.Ok(MapToDto(route));
    }

    /// <summary>
    /// The trunk guard of a route write (design D1): each supplied trunk id must be a trunk of the caller's tenant,
    /// looked up through the tenant-scoped <see cref="TrunkStoreBase.GetAsync"/>. A trunk of another tenant and an id
    /// that names no trunk get the same 400 body, so the answer reveals nothing about another tenant. Returns null
    /// when every supplied id passes. A trunk's tenant never changes, so the check holds at the write.
    /// </summary>
    private static async Task<BadRequest<ErrorResponse>?> RefuseTrunksOutsideTenantAsync(
        TrunkStoreBase trunkStore, long? trunkId, long? overflowTrunkId, string tenantId, CancellationToken ct)
    {
        if (trunkId.HasValue && await trunkStore.GetAsync(trunkId.Value, tenantId, ct) is null)
            return TypedResults.BadRequest(new ErrorResponse("trunkId is not a trunk of this tenant"));
        if (overflowTrunkId.HasValue && await trunkStore.GetAsync(overflowTrunkId.Value, tenantId, ct) is null)
            return TypedResults.BadRequest(new ErrorResponse("overflowTrunkId is not a trunk of this tenant"));
        return null;
    }

    private static async Task<IResult> DeleteRoute(
        long id,
        HttpContext context,
        [FromServices] OutboundRouteStoreBase routeStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var before = await routeStore.GetAsync(id, tenantId, ct);
        await routeStore.DeleteAsync(id, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "outbound_route.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "outbound_route",
            changes: new AuditChanges(Before: before is null ? null : MapToDto(before), After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ReorderRoutes(
        HttpContext context,
        long[] orderedIds,
        OutboundRouteStoreBase routeStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await routeStore.ReorderAsync(orderedIds, tenantId, ct);
        return Results.Ok();
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static OutboundRouteDto MapToDto(OutboundRoute r) =>
        new(r.Id, r.CampaignId, r.Pattern, r.PatternType.ToString(), r.TrunkId,
            r.OverflowTrunkId, r.DialPrefix, r.Priority);

    private static PatternType ParsePatternType(string patternType) =>
        patternType.ToLowerInvariant() switch
        {
            "digitmask" or "digit_mask" => PatternType.DigitMask,
            "regex" => PatternType.Regex,
            "prefix" => PatternType.Prefix,
            _ => PatternType.Prefix,
        };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ─────────────────────────────────────────────────────

internal sealed record CreateOutboundRouteRequest(
    long? CampaignId, string Pattern, string PatternType, long TrunkId,
    long? OverflowTrunkId, string? DialPrefix, int Priority);

internal sealed record UpdateOutboundRouteRequest(
    long? CampaignId, string? Pattern, string? PatternType, long? TrunkId,
    long? OverflowTrunkId, string? DialPrefix, int? Priority);

internal sealed record OutboundRouteDto(
    long Id, long? CampaignId, string Pattern, string PatternType, long TrunkId,
    long? OverflowTrunkId, string? DialPrefix, int Priority);
