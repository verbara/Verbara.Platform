using System.Security.Claims;

namespace Verbara.Platform.Core.Push;

/// <summary>
/// The account a live connection belongs to: the key under which <see cref="LiveConnectionRegistry"/>
/// tracks it, a status lookup checks it, and a <see cref="UserAccessRevokedEvent"/> names it.
/// </summary>
public readonly record struct LiveConnectionOwner(string TenantId, string UserId)
{
    /// <summary>
    /// Resolves the owning account from a principal authenticated by Platform.Api's credentials.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>Access token: <c>sub</c> in tenant <c>tid</c>.</description></item>
    ///   <item><description>Impersonation token: <c>tid</c> is the TARGET tenant; the account is
    ///   <c>sub</c> in its home tenant <c>impersonator_tenant</c>.</description></item>
    ///   <item><description>User-bound API key: <c>user_id</c> in <c>tenant_id</c>. The
    ///   <see cref="ClaimTypes.NameIdentifier"/> claim holds the KEY id there and is never read.</description></item>
    /// </list>
    /// </remarks>
    /// <returns><see langword="null"/> when the principal names no user (e.g. an owner-less management key).</returns>
    public static LiveConnectionOwner? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return null;

        var userId = principal.FindFirst("user_id")?.Value
            ?? principal.FindFirst("sub")?.Value;
        var tenantId = principal.FindFirst("impersonator_tenant")?.Value
            ?? principal.FindFirst("tid")?.Value
            ?? principal.FindFirst("tenant_id")?.Value;

        return string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tenantId)
            ? null
            : new LiveConnectionOwner(tenantId, userId);
    }
}
