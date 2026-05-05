using Verbara.Platform.Api.Services;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class PartnerSettingsEndpoints
{
    public static RouteGroupBuilder MapPartnerSettingsEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner/settings")
            .WithTags("Partner - Settings")
            .RequireAuthorization("PartnerAdminOnly");

        group.MapGet("/", GetPartnerSettings)
            .RequireAuthorization("partner:settings:view");
        group.MapPut("/", UpdatePartnerSettings)
            .RequireAuthorization("partner:settings:manage");

        return app;
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetPartnerSettings(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            callerTenantId, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, brandingStore, ct);

        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    // ── PUT ───────────────────────────────────────────────────────────────────

    private static async Task<IResult> UpdatePartnerSettings(
        HttpContext context,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        [FromServices] FeatureGateCache featureGateCache,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        // Partners can only update Operational and Auth settings — strip everything else
        var sanitized = body with { Plan = null, Quotas = null, RateLimitTier = null, AddOns = null };

        var actorName = context.User.Identity?.Name ?? "unknown";
        var error = await TenantSettingsEndpoints.ApplyUpdates(
            callerTenantId, sanitized, tenantStore, authConfigStore, quotaStore, retentionStore,
            tierCache, featureGateCache, addOnStore, brandingStore, ct,
            context.RequestServices, actorName);
        if (error is not null)
            return error;

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            callerTenantId, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, brandingStore, ct);

        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }
}
