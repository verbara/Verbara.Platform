using System.Security.Claims;
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
        // Management API keys bypass all checks
        var keyTypeClaim = context.User.FindFirst("key_type")?.Value;
        if (keyTypeClaim == "management")
        {
            context.Succeed(requirement);
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
            // Check if user's tenant is a Partner (can manage its own children)
            var userTenant = await _tenantStore.GetAsync(tenantIdClaim);
            if (userTenant is null || userTenant.Type != TenantType.Partner)
                return; // Not host, not partner — deny
        }

        // If a specific permission is required, check it
        if (requirement.Permission is not null)
        {
            var userIdClaim = context.User.FindFirst("user_id")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userIdClaim))
                return;

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
}
