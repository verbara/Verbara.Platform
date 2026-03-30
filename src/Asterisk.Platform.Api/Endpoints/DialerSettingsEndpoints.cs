using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Models;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class DialerSettingsEndpoints
{
    public static void MapDialerSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/dialer/settings").RequireAuthorization("AdminOnly");

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
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var settings = await store.GetDialerSettingsAsync(tenantId, ct);

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
