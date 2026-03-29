using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/audit").RequireAuthorization("AdminOnly");

        group.MapGet("/", SearchAuditLog);
        group.MapGet("/{entityType}/{entityId}", GetEntityHistory);
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<IResult> SearchAuditLog(
        HttpContext context,
        [FromServices] IAuditStore store,
        string? action = null,
        string? entityType = null,
        string? performedBy = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var query = new AuditQuery(
            Action: action,
            EntityType: entityType,
            PerformedBy: performedBy,
            From: from,
            To: to,
            Page: page,
            PageSize: pageSize);

        var result = await store.SearchAsync(tenantId, query, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetEntityHistory(
        string entityType,
        string entityId,
        HttpContext context,
        [FromServices] IAuditStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var entries = await store.GetByEntityAsync(tenantId, entityType, entityId, ct);
        return Results.Ok(entries);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
