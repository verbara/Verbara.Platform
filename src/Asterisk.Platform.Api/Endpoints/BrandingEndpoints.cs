using Asterisk.Platform.Core.Branding;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

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

        branding.MapGet("/{tenantId}", GetByTenantId);
        branding.MapGet("/by-subdomain/{subdomain}", GetBySubdomain);

        return group;
    }

    // ── GET /branding/{tenantId} ──────────────────────────────────────────────

    private static async Task<IResult> GetByTenantId(
        string tenantId,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var branding = await brandingStore.GetAsync(tenantId, ct);
        return branding is null
            ? Results.NotFound()
            : Results.Ok(ToPublicDto(branding));
    }

    // ── GET /branding/by-subdomain/{subdomain} ────────────────────────────────

    private static async Task<IResult> GetBySubdomain(
        string subdomain,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var branding = await brandingStore.GetBySubdomainAsync(subdomain, ct);
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
