using System.Security.Claims;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Canonical caller-identity resolution shared across every endpoint/audit call-site
/// (audit-trail-integrity-fixes, fix 3). This is the SAME claim-order precedence the
/// v2.14.1 impersonation claim-order fix (PR #78) established for
/// <c>ManagementImpersonationEndpoints.ResolveCallerUserId</c>,
/// <c>PlatformAdminAuthorizationHandler</c>, and <c>PermissionAuthorizationHandler</c> — extracted
/// here so every caller (including <c>RecordAudit</c> helpers) shares one order instead of each
/// re-declaring its own copy that can drift.
/// </summary>
internal static class CallerIdentity
{
    /// <summary>
    /// Resolves the calling principal's user id using the canonical claim order
    /// <c>user_id ?? NameIdentifier ?? sub</c>.
    ///
    /// <para>
    /// The <c>user_id</c> claim MUST win: for API-key callers
    /// <see cref="ClaimTypes.NameIdentifier"/> carries the <i>key id</i>
    /// (<c>ApiKeyAuthenticationHandler</c> sets it from <c>apiKey.KeyId.Value</c>), while the
    /// owning user is in <c>user_id</c>. Resolving <c>NameIdentifier</c> first would feed the key
    /// id into per-tenant permission / audit-actor lookups — failing impersonation checks closed
    /// for management keys and mis-attributing audit records to the key id instead of the owning
    /// user (or a generic "system" fallback when no <c>sub</c> claim is present at all, e.g. a bare
    /// API-key caller with only <c>user_id</c>).
    /// </para>
    /// </summary>
    /// <returns>The resolved user id, or <see langword="null"/> when no claim in the order is present.</returns>
    public static string? ResolveUserId(ClaimsPrincipal user)
        => user.FindFirstValue("user_id")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

    /// <summary>
    /// Same resolution as <see cref="ResolveUserId"/>, falling back to <c>"system"</c> so an audit
    /// record always carries a non-empty actor even when no identity claim is resolvable (should
    /// not happen for an authenticated caller, but keeps <c>IAuditService.RecordAsync</c>'s
    /// non-empty-actorId precondition satisfied defensively).
    /// </summary>
    public static string ResolveUserIdOrSystem(ClaimsPrincipal user)
        => ResolveUserId(user) ?? "system";

    /// <summary>
    /// Adds the impersonation context of <paramref name="caller"/> to an audit entry's metadata:
    /// under impersonation the resolved actor id is the impersonating admin's, from another tenant,
    /// so the entry also names that tenant and the impersonation session. Adds nothing otherwise.
    /// </summary>
    public static void AddImpersonationContext(IDictionary<string, string> metadata, ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(caller);

        if (!string.Equals(caller.FindFirstValue("impersonation"), "true", StringComparison.Ordinal))
            return;
        var present = s_impersonationClaims
            .Select(claimType => (Type: claimType, Value: caller.FindFirstValue(claimType)))
            .Where(claim => !string.IsNullOrEmpty(claim.Value));
        foreach (var (claimType, value) in present)
            metadata[claimType] = value!;
    }

    private static readonly string[] s_impersonationClaims =
        ["impersonator_id", "impersonator_tenant", "impersonation_session_id"];
}
