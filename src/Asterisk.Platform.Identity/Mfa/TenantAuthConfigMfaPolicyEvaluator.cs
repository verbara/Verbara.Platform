namespace Asterisk.Platform.Identity.Mfa;

public sealed class TenantAuthConfigMfaPolicyEvaluator : IMfaPolicyEvaluator
{
    private readonly ITenantAuthConfigStore _configStore;

    public TenantAuthConfigMfaPolicyEvaluator(ITenantAuthConfigStore configStore)
    {
        _configStore = configStore;
    }

    /// <inheritdoc/>
    public async Task<bool> RequiresMfaAsync(string tenantId, UserRole role, CancellationToken ct)
    {
        var cfg = await _configStore.GetAsync(tenantId, ct);
        return cfg?.IsMfaRequiredForRole(role.ToString()) == true;
    }
}
