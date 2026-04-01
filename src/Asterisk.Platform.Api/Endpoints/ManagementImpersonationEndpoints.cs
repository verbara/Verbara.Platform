using System.Security.Claims;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementImpersonationEndpoints
{
    public static void MapManagementImpersonationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management").RequireAuthorization("PlatformAdminOnly");

        group.MapPost("/impersonate", StartImpersonation);
        group.MapDelete("/impersonate", EndImpersonation);
    }

    private static async Task<IResult> StartImpersonation(
        [FromBody] ImpersonateRequest body,
        HttpContext context,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IUserStore userStore,
        [FromServices] JwtTokenService jwtTokenService,
        [FromServices] AuthEventService authEventService,
        CancellationToken ct)
    {
        var callerUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        var callerTenantId = context.User.FindFirstValue("tid")
            ?? context.User.FindFirstValue("tenant_id");

        if (string.IsNullOrEmpty(callerUserId) || string.IsNullOrEmpty(callerTenantId))
            return Results.Unauthorized();

        // Verify caller has impersonate permission
        var callerPermissions = await permissionResolver.ResolveAsync(
            new TenantId(callerTenantId), EntityId.From(callerUserId), ct);

        if (!PermissionResolver.HasPermission(callerPermissions, "platform:tenant:impersonate"))
            return Results.Forbid();

        // Validate target tenant exists
        var targetTenant = await tenantStore.GetAsync(body.TargetTenantId, ct);
        if (targetTenant is null)
            return Results.NotFound(new ErrorResponse($"Tenant '{body.TargetTenantId}' not found."));

        // Cannot impersonate the host/platform tenant
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        if (hostTenant is not null && targetTenant.TenantId == hostTenant.TenantId)
            return Results.BadRequest(new ErrorResponse("Cannot impersonate the platform tenant."));

        // Target must be active
        if (targetTenant.Status != TenantStatus.Active)
            return Results.BadRequest(new ErrorResponse("Target tenant is not active."));

        // Get the admin user
        var adminUser = await userStore.GetByIdAsync(
            new TenantId(callerTenantId), EntityId.From(callerUserId), ct);
        if (adminUser is null)
            return Results.NotFound(new ErrorResponse("Admin user not found."));

        // Target permissions: caller's permissions minus platform:* scoped ones
        var targetPermissions = new HashSet<string>(
            callerPermissions.Where(p => !p.StartsWith("platform:", StringComparison.Ordinal)));

        // Generate shadow JWT
        var (token, expiresAt) = jwtTokenService.GenerateImpersonationToken(
            adminUser, body.TargetTenantId, targetPermissions);

        // Audit log
        await authEventService.LogAsync(
            callerTenantId,
            callerUserId,
            AuthEventTypes.ImpersonationStarted,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent,
            new { targetTenantId = body.TargetTenantId, targetTenantName = targetTenant.Name },
            ct);

        return Results.Ok(new ImpersonateResponse(token, expiresAt, body.TargetTenantId, targetTenant.Name));
    }

    private static async Task<IResult> EndImpersonation(
        HttpContext context,
        [FromServices] AuthEventService authEventService,
        CancellationToken ct)
    {
        var isImpersonating = context.User.FindFirstValue("impersonation") == "true";
        if (!isImpersonating)
            return Results.BadRequest(new ErrorResponse("Not currently impersonating."));

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        var impersonatorTenant = context.User.FindFirstValue("impersonator_tenant");

        await authEventService.LogAsync(
            impersonatorTenant ?? "",
            userId,
            AuthEventTypes.ImpersonationEnded,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent,
            null,
            ct);

        return Results.NoContent();
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record ImpersonateRequest(string TargetTenantId);
internal sealed record ImpersonateResponse(string AccessToken, DateTimeOffset ExpiresAt, string TargetTenantId, string TargetTenantName);
