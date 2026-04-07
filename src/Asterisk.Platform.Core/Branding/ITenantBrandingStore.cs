namespace Asterisk.Platform.Core.Branding;

public interface ITenantBrandingStore
{
    ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default);
    ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default);
    ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default);
}
