using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class BrandingEndpoints
{
    internal sealed record PublicBrandingDto(
        string? DisplayName,
        string? LogoUrl,
        string? FaviconUrl,
        string? PrimaryColor,
        string? SecondaryColor,
        string? AccentColor,
        string? Locale,
        string? Timezone);

    internal static RouteGroupBuilder MapBrandingEndpoints(this RouteGroupBuilder group)
    {
        var branding = group.MapGroup("/branding").WithTags("Branding");
        // NO .RequireAuthorization() — public endpoints used before login

        branding.MapGet("/{tenantId}", GetByTenantId)
            .Produces<PublicBrandingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        branding.MapGet("/by-subdomain/{subdomain}", GetBySubdomain)
            .Produces<PublicBrandingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    // ── GET /branding/{tenantId} ──────────────────────────────────────────────

    // The tenant's effective branding (own row, then up to two ancestors). A deleted tenant answers the
    // same 404 as an unknown id: deletion leaves the row in place, so the status is checked here.
    private static async Task<IResult> GetByTenantId(
        string tenantId,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant?.Status == TenantStatus.Deleted)
            return Results.NotFound();

        var branding = await EffectiveBranding.ResolveAsync(tenantId, tenant, tenantStore, brandingStore, ct);
        return branding is null
            ? Results.NotFound()
            : Results.Ok(ToPublicDto(branding));
    }

    // ── GET /branding/by-subdomain/{subdomain} ────────────────────────────────

    // The subdomain names only the row that carries it, never a tenant id: a Customer with no row is
    // found by the console's host guess through GET /branding/{tenantId} instead.
    private static async Task<IResult> GetBySubdomain(
        string subdomain,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var row = await brandingStore.GetBySubdomainAsync(subdomain, ct);
        if (row is null)
            return Results.NotFound();

        var tenant = await tenantStore.GetAsync(row.TenantId, ct);
        if (tenant?.Status == TenantStatus.Deleted)
            return Results.NotFound();

        var branding = await EffectiveBranding.ResolveAsync(row.TenantId, tenant, tenantStore, brandingStore, ct);
        return branding is null
            ? Results.NotFound()
            : Results.Ok(ToPublicDto(branding));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static PublicBrandingDto ToPublicDto(TenantBranding b) =>
        new(b.DisplayName, b.LogoUrl, b.FaviconUrl,
            b.PrimaryColor, b.SecondaryColor, b.AccentColor,
            b.Locale, b.Timezone);
}
