using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// A tenant's effective branding: the one merge rule shared by the public branding endpoints and the
/// settings view's branding block.
/// </summary>
/// <remarks>
/// <para>
/// Each field is the first value that is neither null nor blank (empty or whitespace only), walking the
/// tenant's own row, then its parent's, then its grandparent's: at most <see cref="MaxBrandingHops"/>
/// ancestors, the depth of the Platform → Partner → Customer hierarchy. A field blank on every row comes
/// out null. Blank counts as unset because the settings updates read null as "leave unchanged", so an
/// empty string is how an administrator clears a field, and it must clear to "inherit".
/// </para>
/// <para>
/// The subdomain is never inherited: it is the own row's or null, so a lookup by subdomain never lands
/// on a tenant through its parent's.
/// </para>
/// <para>
/// The walk stops at a null parent, at a parent the tenant store does not know, on a repeated id, or
/// after the last hop. A row whose tenant has no record stands alone. A <see cref="TenantStatus.Deleted"/>
/// ancestor still counts as a hop and its parent pointer is followed, but its row is not read, so a
/// Customer under a deleted Partner takes the Platform's values. Every other status reads as active.
/// The requested tenant's own row is read whatever its status: refusing a deleted tenant is the
/// public endpoints' decision, not this helper's.
/// </para>
/// </remarks>
internal static class EffectiveBranding
{
    /// <summary>The most ancestors whose rows are read: Customer → Partner → Platform.</summary>
    internal const int MaxBrandingHops = 2;

    /// <summary>
    /// Computes the effective branding of <paramref name="tenantId"/>, or null when no row exists in its
    /// chain. Returns a new instance; the store's rows are never modified.
    /// </summary>
    /// <param name="tenantId">The tenant whose branding is computed; the result carries this id.</param>
    /// <param name="tenant">
    /// The tenant's record as the caller read it from <paramref name="tenants"/>, or null when the store does not
    /// know <paramref name="tenantId"/>. Every caller reads it first (the endpoints for the status check, the
    /// settings view to build the rest of the response), so it is never read again here; a null stands for an
    /// unknown tenant, whose row, if any, stands alone.
    /// </param>
    /// <param name="tenants">Read for the parent pointers and the ancestors' status.</param>
    /// <param name="brandings">Read once per tenant in the chain whose row counts.</param>
    /// <param name="ct">Cancels the reads.</param>
    internal static async ValueTask<TenantBranding?> ResolveAsync(
        string tenantId,
        Tenant? tenant,
        ITenantStore tenants,
        ITenantBrandingStore brandings,
        CancellationToken ct)
    {
        var own = await brandings.GetAsync(tenantId, ct);

        var ancestors = new List<TenantBranding>(MaxBrandingHops);
        if (tenant is not null)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { tenantId };
            var parentId = tenant.ParentTenantId;
            for (var hop = 0; hop < MaxBrandingHops && parentId is not null; hop++)
            {
                if (!visited.Add(parentId))
                    break;

                var parent = await tenants.GetAsync(parentId, ct);
                if (parent is null)
                    break;

                if (parent.Status != TenantStatus.Deleted)
                {
                    var row = await brandings.GetAsync(parentId, ct);
                    if (row is not null)
                        ancestors.Add(row);
                }

                parentId = parent.ParentTenantId;
            }
        }

        if (own is null && ancestors.Count == 0)
            return null;

        var parentRow = ancestors.Count > 0 ? ancestors[0] : null;
        var grandparentRow = ancestors.Count > 1 ? ancestors[1] : null;

        string? Pick(Func<TenantBranding, string?> field) =>
            FirstSet(own is null ? null : field(own),
                parentRow is null ? null : field(parentRow),
                grandparentRow is null ? null : field(grandparentRow));

        return new TenantBranding
        {
            TenantId = tenantId,
            DisplayName = Pick(static b => b.DisplayName),
            LogoUrl = Pick(static b => b.LogoUrl),
            FaviconUrl = Pick(static b => b.FaviconUrl),
            PrimaryColor = Pick(static b => b.PrimaryColor),
            SecondaryColor = Pick(static b => b.SecondaryColor),
            AccentColor = Pick(static b => b.AccentColor),
            Locale = Pick(static b => b.Locale),
            Timezone = Pick(static b => b.Timezone),
            Subdomain = string.IsNullOrWhiteSpace(own?.Subdomain) ? null : own.Subdomain,
            SupportEmail = Pick(static b => b.SupportEmail),
            SupportUrl = Pick(static b => b.SupportUrl),
            EmailFromName = Pick(static b => b.EmailFromName),
            EmailFromAddress = Pick(static b => b.EmailFromAddress),
            CreatedAt = own?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = own?.UpdatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    private static string? FirstSet(string? own, string? parent, string? grandparent) =>
        !string.IsNullOrWhiteSpace(own) ? own
        : !string.IsNullOrWhiteSpace(parent) ? parent
        : !string.IsNullOrWhiteSpace(grandparent) ? grandparent
        : null;
}
