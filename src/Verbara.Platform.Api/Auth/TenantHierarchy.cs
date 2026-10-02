using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Auth;

/// <summary>
/// The one parent-chain walk every cross-tenant gate shares.
/// </summary>
/// <remarks>
/// <para>
/// The walk itself is the one written for the impersonation surface
/// (<c>ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync</c>, which now
/// delegates here). It moved into <c>Auth</c> because the authorization handler and the
/// tenant-boundary middleware need the same answer and must not each grow their own copy:
/// two implementations of "is this tenant mine" is how one of them ends up checking
/// <see cref="TenantType"/> only — the shape of the Partner-admin escalation this type
/// exists to close.
/// </para>
/// <para>
/// Fail-closed on every ambiguity: a missing tenant, a broken parent pointer, a cycle, or a
/// chain deeper than <see cref="MaxWalkDepth"/> all return <see langword="false"/>.
/// </para>
/// </remarks>
internal static class TenantHierarchy
{
    /// <summary>
    /// Upper bound on the parent-chain walk. Real hierarchies are two or three levels
    /// (Platform → Partner → Customer); the bound is a guard against corrupt parent
    /// pointers, not a product limit.
    /// </summary>
    internal const int MaxWalkDepth = 16;

    /// <summary>
    /// Walks <see cref="Tenant.ParentTenantId"/> upward from <paramref name="targetTenantId"/>
    /// and returns <see langword="true"/> iff <paramref name="callerTenantId"/> is reached.
    /// The caller's own tenant counts as in-hierarchy.
    /// </summary>
    internal static async Task<bool> IsInCallerHierarchyAsync(
        ITenantStore tenantStore,
        string callerTenantId,
        string targetTenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(callerTenantId) || string.IsNullOrEmpty(targetTenantId))
            return false;

        // Self short-circuit.
        if (string.Equals(callerTenantId, targetTenantId, StringComparison.Ordinal))
            return true;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = targetTenantId;

        for (var i = 0; i < MaxWalkDepth; i++)
        {
            if (!visited.Add(current))
                return false; // cycle detected — fail closed

            var tenant = await tenantStore.GetAsync(current, ct);
            if (tenant is null)
                return false; // broken chain — fail closed

            if (string.IsNullOrEmpty(tenant.ParentTenantId))
                return false; // reached root without matching caller

            if (string.Equals(tenant.ParentTenantId, callerTenantId, StringComparison.Ordinal))
                return true;

            current = tenant.ParentTenantId;
        }

        // Walked past MaxWalkDepth without finding caller — fail closed
        return false;
    }
}
