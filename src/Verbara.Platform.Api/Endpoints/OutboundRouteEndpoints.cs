using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
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

    private static async Task<IResult> ListRoutes(
        HttpContext context,
        [FromServices] OutboundRouteStoreBase routeStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await routeStore.ListByPriorityAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetRoute(
        long id,
        HttpContext context,
        [FromServices] OutboundRouteStoreBase routeStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var route = await routeStore.GetAsync(id, tenantId, ct);
        return route is null ? Results.NotFound() : Results.Ok(MapToDto(route));
    }

    private static async Task<IResult> CreateRoute(
        HttpContext context,
        [FromBody] CreateOutboundRouteRequest body,
        OutboundRouteStoreBase routeStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
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
        return Results.Created($"/admin/routes/{id}", MapToDto(route));
    }

    private static async Task<IResult> UpdateRoute(
        long id,
        HttpContext context,
        [FromBody] UpdateOutboundRouteRequest body,
        OutboundRouteStoreBase routeStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var route = await routeStore.GetAsync(id, tenantId, ct);
        if (route is null)
            return Results.NotFound();

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
        return Results.Ok(MapToDto(route));
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
