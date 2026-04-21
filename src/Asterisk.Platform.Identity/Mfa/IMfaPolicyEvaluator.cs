namespace Asterisk.Platform.Identity.Mfa;

public interface IMfaPolicyEvaluator
{
    /// <summary>
    /// Returns true when the tenant's auth policy requires MFA for the given role.
    /// A tenant with no policy configured returns false (default "optional").
    /// </summary>
    Task<bool> RequiresMfaAsync(string tenantId, UserRole role, CancellationToken ct);
}
