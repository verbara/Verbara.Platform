using System.Security.Claims;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Authorization;

namespace Verbara.Platform.Api.Auth;

internal sealed class PartnerAdminAuthorizationHandler : AuthorizationHandler<PartnerAdminRequirement>
{
    private readonly ITenantStore _tenantStore;
    private readonly PermissionResolver _resolver;

    public PartnerAdminAuthorizationHandler(ITenantStore tenantStore, PermissionResolver resolver)
    {
        _tenantStore = tenantStore;
        _resolver = resolver;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PartnerAdminRequirement requirement)
    {
        var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tenantIdClaim))
            return;

        var tenant = await _tenantStore.GetAsync(tenantIdClaim);
        if (tenant is null || tenant.Type != TenantType.Partner)
            return;

        // Partner must be in operational status (Active, Warning, or Degraded — not Suspended/Deleted/PendingDeletion)
        if (tenant.Status is TenantStatus.Suspended or TenantStatus.Deleted or TenantStatus.PendingDeletion)
            return;

        // If a specific permission is required, check it
        if (requirement.Permission is not null)
        {
            var userIdClaim = context.User.FindFirst("user_id")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userIdClaim))
                return;

            var tenantId = new TenantId(tenantIdClaim);
            var userId = EntityId.From(userIdClaim);
            var permissions = await _resolver.ResolveAsync(tenantId, userId, CancellationToken.None);
            if (!PermissionResolver.HasPermission(permissions, requirement.Permission))
                return;
        }

        context.Succeed(requirement);
    }
}
