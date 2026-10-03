using System.Security.Claims;

namespace Verbara.Platform.Api.Auth;

/// <summary>
/// What an impersonation token passes on a permission gate: exactly the permissions minted into it.
/// </summary>
/// <remarks>
/// <para>
/// <c>StartImpersonation</c> mints the impersonator's own permissions without the platform-scoped
/// ones, or their read-only subset for a read-only session, as the token's <c>permissions</c> claims.
/// Every authorization handler that checks a permission holds an impersonation token to that set.
/// </para>
/// <para>
/// Nothing else stands in for a minted permission: not the token's <c>Admin</c> role, which every
/// impersonation token carries, and not a role lookup, because the token's subject is the impersonator,
/// who holds no roles in the tenant the token acts in.
/// </para>
/// </remarks>
internal static class ImpersonationPermissions
{
    /// <summary>Whether impersonation token <paramref name="principal"/> carries <paramref name="permission"/>.</summary>
    internal static bool Grants(ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        foreach (var claim in principal.FindAll("permissions"))
        {
            if (string.Equals(claim.Value, permission, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
