using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Endpoints;

internal static class GdprEndpoints
{
    public static void MapGdprEndpoints(this IEndpointRouteBuilder app)
    {
        // Tenant admin endpoints
        var admin = app.MapGroup("/admin/gdpr").RequireAuthorization("AdminOnly");
        admin.MapPost("/export", ExportContactData);
        admin.MapPost("/purge", PurgeContactData);
        admin.MapPost("/purge-user", PurgeUserData);
        admin.MapGet("/purge-preview", PurgePreview);

        // Platform admin endpoints
        var mgmt = app.MapGroup("/management/gdpr").RequireAuthorization("PlatformAdminOnly");
        mgmt.MapGet("/purge-log", ListPurgeLog);

        // Retention policy endpoints (under existing management tenants path)
        var retention = app.MapGroup("/management/tenants/{tenantId}").RequireAuthorization("PlatformAdminOnly");
        retention.MapGet("/retention", GetRetentionPolicy);
        retention.MapPut("/retention", UpdateRetentionPolicy);
    }

    // --- Export ---------------------------------------------------------------

    private static async Task<IResult> ExportContactData(
        HttpContext context,
        [FromBody] GdprExportRequest body,
        [FromQuery] string? format,
        [FromServices] IGdprExportService exportService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ContactId))
            return Results.BadRequest(new ErrorResponse("contactId is required"));

        var tenantId = GetTenantId(context);
        var result = await exportService.ExportContactDataAsync(tenantId.Value, body.ContactId, ct);

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "json" : format.ToLowerInvariant();

        IResult response;
        if (normalizedFormat == "csv")
        {
            var formatter = context.RequestServices.GetRequiredKeyedService<IGdprExportFormatter>("csv");
            var exportData = BuildExportData(result, body.ContactId);
            var bytes = await formatter.FormatAsync(exportData, ct);
            var fileName = $"gdpr-export-{body.ContactId}-{DateTimeOffset.UtcNow:yyyyMMdd}{formatter.FileExtension}";
            response = Results.File(bytes, formatter.ContentType, fileName);
        }
        else
        {
            response = Results.Ok(result);
        }

        var notificationService = context.RequestServices.GetService<Asterisk.Platform.Api.Services.NotificationService>();
        if (notificationService is not null)
        {
            _ = notificationService.CreateAsync(
                tenantId.Value, "gdpr.export_completed",
                "Data Export Ready",
                $"Data export for contact {body.ContactId} is ready for download.",
                "/admin/gdpr",
                CancellationToken.None);
        }

        return response;
    }

    // --- Purge (contact) ------------------------------------------------------

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

        var notificationService = context.RequestServices.GetService<Asterisk.Platform.Api.Services.NotificationService>();
        if (notificationService is not null)
        {
            _ = notificationService.CreateAsync(
                tenantId.Value, "gdpr.purge_completed",
                "Data Purge Completed",
                $"Data purge for contact {body.ContactId} has been completed.",
                "/admin/gdpr",
                CancellationToken.None);
        }

        return Results.Ok(result);
    }

    // --- Purge (user) ---------------------------------------------------------

    private static async Task<IResult> PurgeUserData(
        HttpContext context,
        [FromBody] GdprUserPurgeRequest body,
        [FromServices] IGdprPurgeService purgeService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.UserId))
            return Results.BadRequest(new ErrorResponse("userId is required"));
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new ErrorResponse("reason is required"));

        var confirmHeader = context.Request.Headers["X-Confirm-Purge"].ToString();
        if (!string.Equals(confirmHeader, "true", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse("X-Confirm-Purge: true header is required to confirm destructive operation"));

        var tenantId = GetTenantId(context);
        var performedBy = context.User.FindFirst("sub")?.Value ?? "unknown";

        var result = await purgeService.PurgeUserDataAsync(
            tenantId.Value, body.UserId, performedBy, body.Reason, ct);

        return Results.Ok(result);
    }

    // --- Purge Preview --------------------------------------------------------

    private static async Task<IResult> PurgePreview(
        HttpContext context,
        [FromQuery] string userId,
        [FromServices] IGdprPurgeService purgeService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Results.BadRequest(new ErrorResponse("userId query parameter is required"));

        var tenantId = GetTenantId(context);
        var preview = await purgeService.PreviewUserPurgeAsync(tenantId.Value, userId, ct);
        return Results.Ok(preview);
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

    /// <summary>
    /// Adapts a <see cref="GdprExportResult"/> (the rich service result) into the
    /// flat <see cref="GdprExportData"/> model consumed by export formatters.
    /// </summary>
    private static GdprExportData BuildExportData(GdprExportResult result, string contactId) =>
        new()
        {
            SubjectId = result.Subject?.ContactId ?? contactId,
            SubjectType = "contact",
            ExportedAt = result.ExportedAt,
        };
}

// --- DTOs --------------------------------------------------------------------

internal sealed record GdprExportRequest(string ContactId);
internal sealed record GdprPurgeRequest(string ContactId, string Reason);
internal sealed record GdprUserPurgeRequest(string UserId, string Reason);

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
