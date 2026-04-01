namespace Asterisk.Platform.Identity.OidcTokenExchange;

public interface IOidcUserProvisioningService
{
    Task<User?> ProvisionOrUpdateAsync(
        string tenantId, OidcClaimsResult claims,
        TenantAuthConfig config, CancellationToken ct);
}
