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
    /// <summary>
    /// Max depth walked when verifying target is in caller's descendant tree.
    /// Prevents pathological cycles in corrupt stores from hanging the endpoint.
    /// Platform → Partner → Customer → Sub-Customer is 3 levels; 16 gives plenty
    /// of slack for deeper hierarchies we might introduce later.
    /// </summary>
    private const int MaxHierarchyWalkDepth = 16;

    private static readonly HashSet<string> ReadOnlyPermissions = new(StringComparer.Ordinal)
    {
        "contacts:contact:view",
        "contacts:conversation:monitor",
        "queues:queue:view",
        "users:user:view",
        "campaigns:campaign:view",
        "reporting:realtime:view",
        "reporting:historical:view",
        "reporting:historical:export",
        "quality:evaluation:view",
        "recording:recording:play",
        "recording:recording:export",
        "routing:skill:view",
        "routing:flow:view",
        "analytics:cdr:view",
        "analytics:cdr:export",
        "analytics:interval:view",
        "system:audit:view",
        "agentassist:session:view",
        "callanalytics:analysis:view",
        "partner:customer:view",
        "partner:billing:view",
        "partner:settings:view",
    };

    public static void MapManagementImpersonationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management").RequireAuthorization("PlatformAdminOnly");

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

        // ── P0 SECURITY CHECK (v1.9.0): verify target is in caller's hierarchy ──
        // Without this check, any tenant admin with platform:tenant:impersonate could
        // impersonate into an arbitrary peer tenant. Platform-tenant admins bypass
        // (by design — they manage every customer tenant).
        var callerTenant = await tenantStore.GetAsync(callerTenantId, ct);
        var callerIsPlatformAdmin = IsPlatformTenantCaller(callerTenant);

        if (!callerIsPlatformAdmin)
        {
            var authorized = await IsTenantInCallerHierarchyAsync(
                tenantStore, callerTenantId, targetTenant.TenantId, ct);
            if (!authorized)
            {
                // Audit the attempt before rejecting — this is a security-critical signal
                // and should be visible to the caller's own tenant admins for review.
                await authEventService.LogAsync(
                    callerTenantId,
                    callerUserId,
                    AuthEventTypes.ImpersonationPrivilegeEscalationAttempted,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers.UserAgent,
                    new Dictionary<string, string>
                    {
                        ["target_tenant_id"] = body.TargetTenantId,
                        ["caller_tenant_id"] = callerTenantId,
                        ["requested_permissions_count"] = callerPermissions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["severity"] = "error",
                        ["mode"] = body.ReadOnly ? "read_only" : "full",
                    },
                    ct);

                return Results.Problem(
                    title: "Impersonation not authorized",
                    detail: "Target tenant is not in your tenant hierarchy.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // Get the admin user
        var adminUser = await userStore.GetByIdAsync(
            new TenantId(callerTenantId), EntityId.From(callerUserId), ct);
        if (adminUser is null)
            return Results.NotFound(new ErrorResponse("Admin user not found."));

        // Target permissions: caller's permissions minus platform:* scoped ones
        var nonPlatformPerms = callerPermissions
            .Where(p => !p.StartsWith("platform:", StringComparison.Ordinal));

        var targetPermissions = body.ReadOnly
            ? new HashSet<string>(nonPlatformPerms.Where(p => ReadOnlyPermissions.Contains(p)))
            : new HashSet<string>(nonPlatformPerms);

        // Generate shadow JWT
        var (token, expiresAt) = jwtTokenService.GenerateImpersonationToken(
            adminUser, body.TargetTenantId, targetPermissions, body.ReadOnly);

        // ── DUAL AUDIT (v1.9.0) ──
        // Entry 1: CALLER tenant — preserves pre-existing behavior for platform/partner
        // admins to see their own impersonation history.
        await authEventService.LogAsync(
            callerTenantId,
            callerUserId,
            AuthEventTypes.ImpersonationStarted,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent,
            new Dictionary<string, string>
            {
                ["targetTenantId"] = body.TargetTenantId,
                ["targetTenantName"] = targetTenant.Name,
                ["mode"] = body.ReadOnly ? "read_only" : "full",
            },
            ct);

        // Entry 2: TARGET tenant — closes the audit-evasion gap. Target-tenant admins
        // MUST be able to see who impersonated into their tenant. Without this, the
        // audit trail was one-sided and invisible to the affected party.
        await authEventService.LogAsync(
            body.TargetTenantId,
            callerUserId,
            AuthEventTypes.ImpersonationTargetAccessed,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent,
            new Dictionary<string, string>
            {
                ["caller_tenant_id"] = callerTenantId,
                ["caller_tenant_name"] = callerTenant?.Name ?? callerTenantId,
                ["target_tenant_id"] = body.TargetTenantId,
                ["read_only"] = body.ReadOnly ? "true" : "false",
                ["permissions_granted_count"] = targetPermissions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["severity"] = "info",
            },
            ct);

        return Results.Ok(new ImpersonateResponse(token, expiresAt, body.TargetTenantId, targetTenant.Name, body.ReadOnly));
    }

    /// <summary>
    /// Returns <see langword="true"/> if the caller's tenant is the Platform host
    /// tenant (<see cref="TenantType.Platform"/>). Platform admins can impersonate
    /// into any customer tenant by design — they operate the whole installation.
    ///
    /// NOTE: Holding <c>platform:*</c> permissions alone is NOT a bypass signal —
    /// the <c>platform:tenant:impersonate</c> permission is required by ALL callers
    /// (Platform AND Partner admins) to reach this endpoint, so using it as the
    /// bypass indicator would collapse the hierarchy check entirely. Partner admins
    /// MUST demonstrate descent of the target tenant from their own tenant.
    /// </summary>
    private static bool IsPlatformTenantCaller(Tenant? callerTenant)
    {
        return callerTenant is not null && callerTenant.Type == TenantType.Platform;
    }

    /// <summary>
    /// Walks the <see cref="Tenant.ParentTenantId"/> chain from <paramref name="targetTenantId"/>
    /// upward and returns <see langword="true"/> iff the caller's tenant is reached.
    /// Caller's own tenant counts as "in hierarchy" (self-impersonation is a degenerate
    /// but not-a-privilege-escalation case; filtered elsewhere if undesirable).
    /// Uses a cycle-guard bounded by <see cref="MaxHierarchyWalkDepth"/> to defend
    /// against corrupt parent pointers.
    /// </summary>
    internal static async Task<bool> IsTenantInCallerHierarchyAsync(
        ITenantStore tenantStore,
        string callerTenantId,
        string targetTenantId,
        CancellationToken ct)
    {
        // Self-impersonation short-circuit
        if (string.Equals(callerTenantId, targetTenantId, StringComparison.Ordinal))
            return true;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = targetTenantId;

        for (var i = 0; i < MaxHierarchyWalkDepth; i++)
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

        // Walked past MaxHierarchyWalkDepth without finding caller — fail closed
        return false;
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

internal sealed record ImpersonateRequest(string TargetTenantId, bool ReadOnly = false);
internal sealed record ImpersonateResponse(string AccessToken, DateTimeOffset ExpiresAt, string TargetTenantId, string TargetTenantName, bool ReadOnly);
