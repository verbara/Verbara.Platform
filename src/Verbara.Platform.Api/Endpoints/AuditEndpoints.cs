using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/audit").RequireAuthorization("AdminOnly");

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
        string? category = null,
        string? severity = null,
        string? actorId = null,
        string? targetId = null,
        string? targetType = null,
        Guid? correlationId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var query = new AuditQuery(
            Action: action,
            EntityType: entityType,
            PerformedBy: performedBy,
            From: from.ToUtcInstant(),
            To: to.ToUtcInstant(),
            Page: page,
            PageSize: pageSize,
            Category: category,
            Severity: severity,
            ActorId: actorId,
            TargetId: targetId,
            TargetType: targetType,
            CorrelationId: correlationId);

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
