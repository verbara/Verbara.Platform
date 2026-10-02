using Microsoft.AspNetCore.Authorization;

namespace Verbara.Platform.Api.Auth;

internal sealed class PlatformAdminRequirement : IAuthorizationRequirement
{
    /// <summary>The host-tenant-only gate. Partner callers never satisfy it.</summary>
    internal const string HostOnlyPolicy = "PlatformAdminOnly";

    /// <summary>
    /// The gate for the two surfaces a Partner may reach on behalf of its own subtree.
    /// See <see cref="AllowPartnerDelegation"/> for what a surface must do to earn it.
    /// </summary>
    internal const string PartnerDelegatedPolicy = "PlatformAdminOrDelegatedPartner";

    public string? Permission { get; }

    /// <summary>
    /// Whether a <c>Partner</c>-type tenant may satisfy this gate at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="false"/>: a <see cref="PlatformAdminRequirement"/> is a
    /// <em>host-tenant</em> gate. The handler used to admit any Partner-tenant caller on every
    /// gate, which handed a Partner admin the whole <c>/management</c> surface — key minting,
    /// JWT rotation, cluster, the full tenant list — across the entire installation, not just
    /// its own subtree. Partners have their own surface for the work they legitimately do:
    /// <c>/partner/*</c>, gated by <c>PartnerAdminOnly</c>, every handler of which already
    /// scopes to the caller's own children.
    /// </para>
    /// <para>
    /// Set it to <see langword="true"/> ONLY on a surface that resolves a target tenant from the
    /// request and checks it against <see cref="TenantHierarchy.IsInCallerHierarchyAsync"/>
    /// itself — today that is <c>POST/DELETE /management/impersonate</c> (target in the body)
    /// and <c>/management/mfa/*</c> (target in <c>?targetTenant=</c>). The flag says "this gate
    /// is not the last line of defence here"; it is never a licence to skip the hierarchy check
    /// downstream. A new surface that forgets the flag fails closed for Partners, which is the
    /// point of making <see langword="false"/> the default.
    /// </para>
    /// </remarks>
    public bool AllowPartnerDelegation { get; }

    public PlatformAdminRequirement(string? permission = null, bool allowPartnerDelegation = false)
    {
        Permission = permission;
        AllowPartnerDelegation = allowPartnerDelegation;
    }
}
