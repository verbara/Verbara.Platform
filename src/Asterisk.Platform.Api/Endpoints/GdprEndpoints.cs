using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class GdprEndpoints
{
    public static void MapGdprEndpoints(this IEndpointRouteBuilder app)
    {
        // Tenant admin endpoints
        var admin = app.MapGroup("/api/admin/gdpr").RequireAuthorization("AdminOnly");
        admin.MapPost("/export", ExportContactData);
        admin.MapPost("/purge", PurgeContactData);

        // Platform admin endpoints
        var mgmt = app.MapGroup("/api/management/gdpr").RequireAuthorization("PlatformAdminOnly");
        mgmt.MapGet("/purge-log", ListPurgeLog);

        // Retention policy endpoints (under existing management tenants path)
        var retention = app.MapGroup("/api/management/tenants/{tenantId}").RequireAuthorization("PlatformAdminOnly");
        retention.MapGet("/retention", GetRetentionPolicy);
        retention.MapPut("/retention", UpdateRetentionPolicy);
    }

    // --- Export ---------------------------------------------------------------

    private static async Task<IResult> ExportContactData(
        HttpContext context,
        [FromBody] GdprExportRequest body,
        [FromServices] IGdprExportService exportService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ContactId))
            return Results.BadRequest(new ErrorResponse("contactId is required"));

        var tenantId = GetTenantId(context);
        var result = await exportService.ExportContactDataAsync(tenantId.Value, body.ContactId, ct);
        return Results.Ok(result);
    }

    // --- Purge ----------------------------------------------------------------

    private static async Task<IResult> PurgeContactData(
        HttpContext context,
        [FromBody] GdprPurgeRequest body,
        [FromServices] IGdprPurgeService purgeService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ContactId))
            return Results.BadRequest(new ErrorResponse("contactId is required"));
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new ErrorResponse("reason is required"));

        var tenantId = GetTenantId(context);
        var userId = context.User.FindFirst("sub")?.Value ?? "unknown";

        var result = await purgeService.PurgeContactDataAsync(
            tenantId.Value, body.ContactId, userId, body.Reason, ct);

        return Results.Ok(result);
    }

    // --- Purge Log ------------------------------------------------------------

    private static async Task<IResult> ListPurgeLog(
        [FromQuery] string? tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] IPurgeLogStore store,
        CancellationToken ct)
    {
        var result = await store.ListAsync(tenantId, from, to, page: 1, pageSize: 50, ct);
        return Results.Ok(result);
    }

    // --- Retention Policy -----------------------------------------------------

    private static async Task<IResult> GetRetentionPolicy(
        string tenantId,
        [FromServices] ITenantRetentionPolicyStore store,
        CancellationToken ct)
    {
        var policy = await store.GetAsync(tenantId, ct);
        if (policy is null)
            return Results.Ok(new RetentionPolicyDto(tenantId, null, null, null, null));

        return Results.Ok(new RetentionPolicyDto(
            policy.TenantId,
            policy.ConversationRetentionDays,
            policy.AuthEventRetentionDays,
            policy.AuditRetentionDays,
            policy.UsageRecordRetentionDays));
    }

    private static async Task<IResult> UpdateRetentionPolicy(
        string tenantId,
        [FromBody] UpdateRetentionPolicyRequest body,
        [FromServices] ITenantRetentionPolicyStore store,
        CancellationToken ct)
    {
        var policy = new TenantRetentionPolicy
        {
            TenantId = tenantId,
            ConversationRetentionDays = body.ConversationRetentionDays,
            AuthEventRetentionDays = body.AuthEventRetentionDays,
            AuditRetentionDays = body.AuditRetentionDays,
            UsageRecordRetentionDays = body.UsageRecordRetentionDays,
        };

        await store.SaveAsync(policy, ct);
        return Results.Ok(new RetentionPolicyDto(
            tenantId,
            policy.ConversationRetentionDays,
            policy.AuthEventRetentionDays,
            policy.AuditRetentionDays,
            policy.UsageRecordRetentionDays));
    }

    // --- Helpers --------------------------------------------------------------

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// --- DTOs --------------------------------------------------------------------

internal sealed record GdprExportRequest(string ContactId);
internal sealed record GdprPurgeRequest(string ContactId, string Reason);

internal sealed record RetentionPolicyDto(
    string TenantId,
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);

internal sealed record UpdateRetentionPolicyRequest(
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);
