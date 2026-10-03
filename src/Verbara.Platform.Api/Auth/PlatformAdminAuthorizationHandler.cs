using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Authorization;

namespace Verbara.Platform.Api.Auth;

internal sealed class PlatformAdminAuthorizationHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly ITenantStore _tenantStore;
    private readonly PermissionResolver _resolver;

    // Cache host tenant ID to avoid repeated lookups
    private string? _cachedHostTenantId;

    public PlatformAdminAuthorizationHandler(ITenantStore tenantStore, PermissionResolver resolver)
    {
        _tenantStore = tenantStore;
        _resolver = resolver;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        // ADMIN-002 (PREPUB-2026-05-09): the legacy short-circuit accepted any
        // management-key request unconditionally — the permission seed on
        // PlatformAdminRequirement(...) was ornamental for that auth path.
        // Now: management keys still bypass the host/Partner/role gate, but
        // a permission-bearing requirement MUST match the key's scopes.
        // Bare PlatformAdminRequirement() (no permission) continues to
        // succeed on any management key (matches /management/api-keys,
        // /management/billing/*, /management/tenants/* — those surfaces are
        // gated by PlatformAdminOnly with no specific permission).
        var keyTypeClaim = context.User.FindFirst("key_type")?.Value;
        if (keyTypeClaim == "management")
        {
            if (requirement.Permission is null)
            {
                context.Succeed(requirement);
                return;
            }

            if (ManagementKeyHasPermission(context, requirement.Permission))
                context.Succeed(requirement);

            // No fall-through to the user-permission path — management keys
            // never carry a tenant role to resolve against.
            return;
        }

        // Resolve user's tenant from claims
        var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tenantIdClaim))
            return;

        // Resolve host tenant (cached)
        var hostTenantId = await GetHostTenantIdAsync();
        if (hostTenantId is null)
            return; // No host tenant exists yet — only /api/setup is accessible

        var isHostTenant = string.Equals(tenantIdClaim, hostTenantId, StringComparison.OrdinalIgnoreCase);

        if (!isHostTenant)
        {
            // A PlatformAdminRequirement is a host-tenant gate unless the surface
            // explicitly delegates to Partners AND checks the target tenant against the
            // caller's hierarchy itself (see PlatformAdminRequirement.AllowPartnerDelegation).
            // The previous version admitted any Partner-tenant caller on EVERY gate, which
            // is how a Partner admin reached /management/api-keys, /management/security/jwt,
            // /management/cluster and the installation-wide tenant list.
            if (!requirement.AllowPartnerDelegation)
                return;

            var userTenant = await _tenantStore.GetAsync(tenantIdClaim);
            if (userTenant is null || userTenant.Type != TenantType.Partner)
                return; // Not host, not partner — deny

            // Parity with PartnerAdminAuthorizationHandler: a Partner whose own tenant is
            // suspended or being deleted administers nothing, including its children.
            if (userTenant.Status is TenantStatus.Suspended or TenantStatus.Deleted or TenantStatus.PendingDeletion)
                return;
        }

        // If a specific permission is required, check it
        if (requirement.Permission is not null)
        {
            // audit-trail-integrity-fixes (fix 3): shared canonical resolver — the same
            // user_id ?? NameIdentifier ?? sub precedence used everywhere else in the Api project.
            var userIdClaim = CallerIdentity.ResolveUserId(context.User);

            if (string.IsNullOrEmpty(userIdClaim))
                return;

            // An impersonation token passes only on a permission minted into it, never on its Admin
            // role (ImpersonationPermissions). The host tenant cannot be impersonated, so such a token
            // reaches this branch only on a Partner-delegated gate, impersonating a Partner tenant.
            if (AccountStatusGate.IsImpersonation(context.User))
            {
                if (!ImpersonationPermissions.Grants(context.User, requirement.Permission))
                    return;
            }
            else
            {
                var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value ?? context.User.FindFirst("role")?.Value;
                if (roleClaim is not ("Admin" or "SystemAdmin"))
                {
                    var tenantId = new TenantId(tenantIdClaim);
                    var userId = EntityId.From(userIdClaim);
                    var permissions = await _resolver.ResolveAsync(tenantId, userId, CancellationToken.None);
                    if (!PermissionResolver.HasPermission(permissions, requirement.Permission))
                        return;
                }
            }
        }
        else
        {
            // No specific permission — require Admin role at minimum
            var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value ?? context.User.FindFirst("role")?.Value;
            if (roleClaim is not ("Admin" or "SystemAdmin"))
                return;
        }

        context.Succeed(requirement);
    }

    private async Task<string?> GetHostTenantIdAsync()
    {
        if (_cachedHostTenantId is not null)
            return _cachedHostTenantId;

        var host = await _tenantStore.GetHostTenantAsync();
        if (host is not null)
            _cachedHostTenantId = host.TenantId;

        return _cachedHostTenantId;
    }

    /// <summary>
    /// ADMIN-002 (PREPUB-2026-05-09): mirror <c>ApiKey.HasScope</c> wildcard
    /// expansion against the principal's <c>scope</c> claims. Three branches:
    /// <list type="bullet">
    /// <item>Exact match: <c>scope = required</c>.</item>
    /// <item>Legacy blanket back-compat: <c>scope == "platform:*"</c> grants
    /// any platform-admin permission. Documented in ADR-0019 as deprecated for
    /// v1.13.x; remove the wildcard interpretation in v1.15.x per the migration
    /// path captured there.</item>
    /// <item>Prefix wildcard: <c>scope</c> ends in <c>":*"</c> and the prefix
    /// matches the start of <paramref name="requiredPermission"/> (mirrors
    /// <c>ApiKey.HasScope</c>).</item>
    /// </list>
    /// </summary>
    private static bool ManagementKeyHasPermission(
        AuthorizationHandlerContext context, string requiredPermission)
    {
        foreach (var claim in context.User.FindAll("scope"))
        {
            var scope = claim.Value;
            if (string.Equals(scope, requiredPermission, StringComparison.Ordinal))
                return true;

            // Legacy "platform:*" blanket — kept through v1.13.x for back-compat.
            if (string.Equals(scope, "platform:*", StringComparison.Ordinal))
                return true;

            // Generic prefix wildcard ("admin:*" matches "admin:foo").
            if (scope.EndsWith(":*", StringComparison.Ordinal))
            {
                var prefix = scope[..^1]; // "admin:*" → "admin:"
                if (requiredPermission.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
