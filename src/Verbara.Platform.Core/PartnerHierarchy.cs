using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Core;

/// <summary>
/// Facts about the Platform → Partner → Customer hierarchy that more than one path needs
/// (partner-channel-pilot-fixes, partner-channel-tenancy, design D16).
/// </summary>
public static class PartnerHierarchy
{
    /// <summary>
    /// The Partner that bills <paramref name="tenantId"/>, when that tenant is a Customer whose parent tenant is a
    /// Partner; null otherwise — a direct Customer (parent: the Platform tenant), a Partner, the Platform tenant, a
    /// tenant that does not exist, or a Customer whose parent row no longer exists.
    /// </summary>
    public static async ValueTask<Tenant?> GetBillingPartnerAsync(ITenantStore store, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var tenant = await store.GetAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant is not { Type: TenantType.Customer, ParentTenantId: { } parentId })
            return null;
        var parent = await store.GetAsync(parentId, ct).ConfigureAwait(false);
        return parent is { Type: TenantType.Partner } ? parent : null;
    }

    /// <summary>
    /// True when <paramref name="tenantId"/> is a Partner's Customer: the Partner bills it outside Verbara, so Verbara
    /// never runs dunning on it (the host dunning routes refuse it, the background sweep skips it).
    /// </summary>
    public static async ValueTask<bool> IsPartnerCustomerAsync(ITenantStore store, string tenantId, CancellationToken ct = default) =>
        await GetBillingPartnerAsync(store, tenantId, ct).ConfigureAwait(false) is not null;
}
