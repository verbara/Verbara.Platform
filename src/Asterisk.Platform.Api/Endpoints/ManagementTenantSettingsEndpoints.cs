using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal sealed record ManagementUpdateTenantSettingsRequest(
    UpdateOperationalSettingsDto? Operational = null,
    UpdateAuthSettingsDto? Auth = null,
    UpdateQuotaSettingsDto? Quotas = null,
    UpdateRetentionSettingsDto? Retention = null,
    RateLimitTier? RateLimitTier = null,
    TenantPlan? Plan = null,
    IReadOnlyList<PlanFeature>? AddOns = null,
    UpdateManagementBrandingSettingsDto? Branding = null);

internal static class ManagementTenantSettingsEndpoints
{
    public static void MapManagementTenantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/tenants/{id}/settings")
            .RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/", GetSettings);
        group.MapPut("/", UpdateSettings);
    }

    private static async Task<IResult> GetSettings(
        string id,
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
        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            id, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, brandingStore, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSettings(
        string id,
        [FromBody] ManagementUpdateTenantSettingsRequest body,
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
        var existing = await tenantStore.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        // Convert to base request (without management branding) for shared ApplyUpdates
        var baseRequest = new UpdateTenantSettingsRequest(
            Operational: body.Operational,
            Auth: body.Auth,
            Quotas: body.Quotas,
            Retention: body.Retention,
            RateLimitTier: body.RateLimitTier,
            Plan: body.Plan,
            AddOns: body.AddOns);

        await TenantSettingsEndpoints.ApplyUpdates(
            id, baseRequest, tenantStore, authConfigStore, quotaStore, retentionStore,
            tierCache, featureGateCache, addOnStore, brandingStore, ct);

        // Apply management branding (includes Subdomain)
        if (body.Branding is not null)
            await TenantSettingsEndpoints.ApplyManagementBrandingUpdates(id, body.Branding, brandingStore, ct);

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            id, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, brandingStore, ct);
        return Results.Ok(dto);
    }
}
