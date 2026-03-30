using System.Security.Claims;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly PermissionResolver _resolver;

    public PermissionAuthorizationHandler(PermissionResolver resolver)
    {
        _resolver = resolver;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // API keys with Admin role get all permissions (backward compat)
        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
        if (roleClaim is "Admin" or "SystemAdmin")
        {
            context.Succeed(requirement);
            return;
        }

        var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        var userIdClaim = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(tenantIdClaim) || string.IsNullOrEmpty(userIdClaim))
        {
            // No tenant or user context -- check if permissions claim exists in JWT
            var permissionsClaim = context.User.FindAll("permissions");
            if (permissionsClaim.Any(c => c.Value == requirement.Permission))
            {
                context.Succeed(requirement);
            }
            return;
        }

        var tenantId = new TenantId(tenantIdClaim);
        var userId = EntityId.From(userIdClaim);

        var permissions = await _resolver.ResolveAsync(tenantId, userId, CancellationToken.None);
        if (PermissionResolver.HasPermission(permissions, requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
