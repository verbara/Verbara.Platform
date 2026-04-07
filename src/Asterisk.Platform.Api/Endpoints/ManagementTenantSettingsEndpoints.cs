using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

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
        CancellationToken ct)
    {
        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            id, tenantStore, authConfigStore, quotaStore, retentionStore, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSettings(
        string id,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] TenantTierCache tierCache,
        CancellationToken ct)
    {
        var existing = await tenantStore.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        await TenantSettingsEndpoints.ApplyUpdates(
            id, body, tenantStore, authConfigStore, quotaStore, retentionStore, tierCache, ct);

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            id, tenantStore, authConfigStore, quotaStore, retentionStore, ct);
        return Results.Ok(dto);
    }
}
