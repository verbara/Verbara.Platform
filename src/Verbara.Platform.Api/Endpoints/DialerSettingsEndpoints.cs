using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class DialerSettingsEndpoints
{
    public static void MapDialerSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/dialer/settings")
            .RequireAuthorization("AdminOnly")
            .RequireLicenseFeature(LicenseFeature.Dialer)
            .RequirePlanFeature(PlanFeature.Dialer);

        group.MapGet("/", GetSettings);
        group.MapPut("/", UpdateSettings);
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<IResult> GetSettings(
        HttpContext context,
        [FromServices] CampaignStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var settings = await store.GetDialerSettingsAsync(tenantId, ct);
        return Results.Ok(MapToDto(settings));
    }

    private static async Task<IResult> UpdateSettings(
        HttpContext context,
        [FromBody] UpdateDialerSettingsRequest body,
        CampaignStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var settings = await store.GetDialerSettingsAsync(tenantId, ct);

        var before = MapToDto(settings);

        if (body.MaxGlobalChannels.HasValue) settings.MaxGlobalChannels = body.MaxGlobalChannels.Value;
        if (body.DefaultRingTimeoutSeconds.HasValue) settings.DefaultRingTimeoutSeconds = body.DefaultRingTimeoutSeconds.Value;
        if (body.CampaignPollIntervalSeconds.HasValue) settings.CampaignPollIntervalSeconds = body.CampaignPollIntervalSeconds.Value;
        if (body.MaxConcurrentCampaigns.HasValue) settings.MaxConcurrentCampaigns = body.MaxConcurrentCampaigns.Value;
        if (body.BlendModeEnabled.HasValue) settings.BlendModeEnabled = body.BlendModeEnabled.Value;
        if (body.JitterMinMs.HasValue) settings.JitterMinMs = body.JitterMinMs.Value;
        if (body.JitterMaxMs.HasValue) settings.JitterMaxMs = body.JitterMaxMs.Value;
        if (body.AhtCacheDurationSeconds.HasValue) settings.AhtCacheDurationSeconds = body.AhtCacheDurationSeconds.Value;
        if (body.ScheduledCallbackPollSeconds.HasValue) settings.ScheduledCallbackPollSeconds = body.ScheduledCallbackPollSeconds.Value;

        await store.UpsertDialerSettingsAsync(settings, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "dialer_settings.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: tenantId, targetType: "dialer_settings",
            changes: new AuditChanges(Before: before, After: MapToDto(settings)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(MapToDto(settings));
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static DialerSettingsDto MapToDto(DialerSettings s) =>
        new(
            s.Id,
            s.MaxGlobalChannels,
            s.DefaultRingTimeoutSeconds,
            s.CampaignPollIntervalSeconds,
            s.MaxConcurrentCampaigns,
            s.BlendModeEnabled,
            s.JitterMinMs,
            s.JitterMaxMs,
            s.AhtCacheDurationSeconds,
            s.ScheduledCallbackPollSeconds);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ────────────────────────────────────────────────────

internal sealed record UpdateDialerSettingsRequest(
    int? MaxGlobalChannels, int? DefaultRingTimeoutSeconds,
    int? CampaignPollIntervalSeconds, int? MaxConcurrentCampaigns,
    bool? BlendModeEnabled, int? JitterMinMs, int? JitterMaxMs,
    int? AhtCacheDurationSeconds, int? ScheduledCallbackPollSeconds);

internal sealed record DialerSettingsDto(
    long Id, int MaxGlobalChannels, int DefaultRingTimeoutSeconds,
    int CampaignPollIntervalSeconds, int MaxConcurrentCampaigns,
    bool BlendModeEnabled, int JitterMinMs, int JitterMaxMs,
    int AhtCacheDurationSeconds, int ScheduledCallbackPollSeconds);
