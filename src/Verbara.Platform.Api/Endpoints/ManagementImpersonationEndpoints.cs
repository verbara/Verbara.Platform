using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class ManagementImpersonationEndpoints
{
    /// <summary>
    /// R5.2 PB.2 — admin authorization policy for the impersonation session
    /// management surface. Wired in <c>Program.cs</c> via
    /// <c>AddPolicy("ImpersonationAdminGate", p =&gt; p.AddRequirements(new
    /// PlatformAdminRequirement("security.impersonation.manage")))</c>.
    /// PlatformAdminRequirement combines host/partner-tenant gating with the
    /// seeded <c>security.impersonation.manage</c> permission so the surface is
    /// double-locked: only platform admins (or partner admins managing their
    /// children) with the explicit permission can list / revoke active
    /// sessions or read history.
    /// </summary>
    public const string AdminAuthorizationPolicy = "ImpersonationAdminGate";


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

        // R5.2 PB.2 — admin session-management surface, double-gated by
        // PlatformAdminRequirement + `security.impersonation.manage`.
        var adminGroup = app.MapGroup("/management/impersonation/sessions")
            .RequireAuthorization(AdminAuthorizationPolicy);

        adminGroup.MapGet("/active", ListActiveSessions);
        adminGroup.MapPost("/{id}/revoke", RevokeSession);
        adminGroup.MapGet("/history", ListSessionHistory);
    }

    private static async Task<IResult> StartImpersonation(
        [FromBody] ImpersonateRequest body,
        HttpContext context,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IUserStore userStore,
        [FromServices] JwtTokenService jwtTokenService,
        [FromServices] AuthEventService authEventService,
        [FromServices] IImpersonationSessionStore sessionStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] TimeProvider clock,
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

        // ── R5.2 PB.2 / C.7: enforce per-tenant max concurrent sessions ──
        var actorAuthConfig = await authConfigStore.GetAsync(callerTenantId, ct);
        var maxConcurrent = actorAuthConfig?.ImpersonationMaxConcurrentSessions
            ?? new TenantAuthConfig { TenantId = callerTenantId }.ImpersonationMaxConcurrentSessions;
        if (maxConcurrent > 0)
        {
            var activeCount = await sessionStore
                .CountActiveByActorTenantAsync(callerTenantId, ct);
            if (activeCount >= maxConcurrent)
            {
                return Results.Problem(
                    title: "Too many active impersonation sessions",
                    detail:
                        $"Tenant '{callerTenantId}' already has {activeCount} active impersonation sessions " +
                        $"(maximum {maxConcurrent}). Revoke an existing session before opening a new one.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // R5.2 PB.2 — allocate the session id BEFORE generating the JWT so the
        // shadow token can carry the link claim. The session record is added
        // afterward so we never persist a session whose token failed to issue.
        var sessionId = Guid.NewGuid().ToString("N");

        // Generate shadow JWT (claims include the session id so EndImpersonation
        // can locate + close the matching record).
        var (token, expiresAt) = jwtTokenService.GenerateImpersonationToken(
            adminUser, body.TargetTenantId, targetPermissions, body.ReadOnly,
            impersonationSessionId: sessionId);

        // Record an admin-visible session so the management UI can list it,
        // the timeout sweep can revoke it, and the manual-revoke endpoint has
        // a stable id to act on.
        await sessionStore.AddAsync(new ImpersonationSession
        {
            Id = sessionId,
            ActorUserId = callerUserId,
            ActorTenantId = callerTenantId,
            TargetUserId = null,
            TargetTenantId = body.TargetTenantId,
            Reason = body.Reason,
            ReadOnly = body.ReadOnly,
            StartedAt = clock.GetUtcNow(),
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
        }, ct);

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

        return Results.Ok(new ImpersonateResponse(
            token, expiresAt, body.TargetTenantId, targetTenant.Name, body.ReadOnly, sessionId));
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
        [FromServices] IImpersonationSessionStore sessionStore,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        var isImpersonating = context.User.FindFirstValue("impersonation") == "true";
        if (!isImpersonating)
            return Results.BadRequest(new ErrorResponse("Not currently impersonating."));

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        var impersonatorTenant = context.User.FindFirstValue("impersonator_tenant");
        var sessionId = context.User.FindFirstValue("impersonation_session_id");

        // R5.2 PB.2 — close the matching session record so the admin list view
        // stops showing it. Idempotent: a session already closed by manual
        // revoke / auto-timeout will short-circuit inside the store.
        if (!string.IsNullOrEmpty(sessionId))
        {
            await sessionStore.RevokeAsync(
                sessionId,
                ImpersonationSessionStatus.Completed,
                reason: "completed",
                endedAt: clock.GetUtcNow(),
                ct);
        }

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

    // ─── R5.2 PB.2 — admin session-management endpoints ───────────────────────

    private static async Task<IResult> ListActiveSessions(
        HttpContext context,
        [FromServices] IImpersonationSessionStore sessionStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantStore tenantStore,
        [FromServices] TimeProvider clock,
        [FromQuery] string? actorTenantId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var (scopeTenant, error) = await ResolveAdminScopeAsync(context, tenantStore, actorTenantId, ct);
        if (error is not null)
            return error;

        var paged = await sessionStore.ListActiveAsync(scopeTenant, page, pageSize, ct);
        var dtos = await MapSessionsAsync(paged.Items, authConfigStore, clock.GetUtcNow(), ct);
        return Results.Ok(new PagedResult<ImpersonationSessionDto>(
            dtos, paged.TotalCount, paged.Page, paged.PageSize));
    }

    private static async Task<IResult> RevokeSession(
        string id,
        HttpContext context,
        [FromBody] RevokeImpersonationSessionRequest? body,
        [FromServices] IImpersonationSessionStore sessionStore,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        var session = await sessionStore.GetAsync(id, ct);
        if (session is null)
            return Results.NotFound(new ErrorResponse($"Impersonation session '{id}' not found."));

        var (scopeTenant, error) = await ResolveAdminScopeAsync(context, tenantStore, session.ActorTenantId, ct);
        if (error is not null)
            return error;
        // Partner admins can only revoke sessions from their own actor tenant
        // (or any actor tenant when scopeTenant is null = PlatformAdmin).
        if (scopeTenant is not null && !string.Equals(scopeTenant, session.ActorTenantId, StringComparison.Ordinal))
            return Results.Forbid();

        var actorUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub")
            ?? "system";
        var actorTenantId = context.User.FindFirstValue("tid")
            ?? context.User.FindFirstValue("tenant_id")
            ?? "platform";

        var reason = string.IsNullOrWhiteSpace(body?.Reason) ? "manual_revoke" : body!.Reason!;
        var revoked = await sessionStore.RevokeAsync(
            id,
            ImpersonationSessionStatus.ManuallyRevoked,
            reason,
            clock.GetUtcNow(),
            ct);

        if (!revoked)
        {
            // Already closed (idempotent — return 204 instead of 409 so the
            // admin UI can collapse double-clicks gracefully).
            return Results.NoContent();
        }

        await audit.RecordAsync(
            new TenantId(session.ActorTenantId),
            category: "auth",
            action: "impersonation.session.revoked",
            severity: "warning",
            actorId: actorUserId,
            actorType: "user",
            targetId: id,
            targetType: "ImpersonationSession",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["actor_tenant_id"] = actorTenantId,
                ["target_tenant_id"] = session.TargetTenantId,
                ["impersonator_user_id"] = session.ActorUserId,
                ["impersonator_tenant_id"] = session.ActorTenantId,
                ["reason"] = reason,
                ["read_only"] = session.ReadOnly ? "true" : "false",
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            },
            ct: ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListSessionHistory(
        HttpContext context,
        [FromServices] IImpersonationSessionStore sessionStore,
        [FromServices] ITenantStore tenantStore,
        [FromServices] TimeProvider clock,
        [FromQuery] string? actorTenantId = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var (scopeTenant, error) = await ResolveAdminScopeAsync(context, tenantStore, actorTenantId, ct);
        if (error is not null)
            return error;

        var paged = await sessionStore.ListHistoryAsync(scopeTenant, from, to, page, pageSize, ct);
        var dtos = paged.Items.Select(s => MapSession(s, autoTimeoutMinutes: 0, now: clock.GetUtcNow())).ToList();
        return Results.Ok(new PagedResult<ImpersonationSessionDto>(
            dtos, paged.TotalCount, paged.Page, paged.PageSize));
    }

    /// <summary>
    /// Resolves the actor-tenant filter for admin session-management endpoints:
    /// PlatformAdmins (host tenant) may pass any tenant or null (= all tenants);
    /// Partner admins are pinned to their own tenant id.
    /// </summary>
    private static async Task<(string? Tenant, IResult? Error)> ResolveAdminScopeAsync(
        HttpContext context,
        ITenantStore tenantStore,
        string? requestedTenant,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirstValue("tid")
            ?? context.User.FindFirstValue("tenant_id");
        if (string.IsNullOrEmpty(callerTenantId))
            return (null, Results.Forbid());

        var callerTenant = await tenantStore.GetAsync(callerTenantId, ct);
        var isPlatformAdmin = IsPlatformTenantCaller(callerTenant);

        if (isPlatformAdmin)
            return (string.IsNullOrWhiteSpace(requestedTenant) ? null : requestedTenant, null);

        // Partner admins can only see / revoke sessions originating from their
        // own tenant — silently scope back rather than 403 so the UI can omit
        // the cross-tenant filter when the caller has no rights to use it.
        return (callerTenantId, null);
    }

    private static async Task<IReadOnlyList<ImpersonationSessionDto>> MapSessionsAsync(
        IReadOnlyList<ImpersonationSession> sessions,
        ITenantAuthConfigStore authConfigStore,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (sessions.Count == 0)
            return Array.Empty<ImpersonationSessionDto>();

        // Cache per-actor-tenant timeout to avoid re-querying for sessions that
        // share an actor tenant (the typical case in a single page view).
        var timeoutCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var dtos = new List<ImpersonationSessionDto>(sessions.Count);
        foreach (var s in sessions)
        {
            if (!timeoutCache.TryGetValue(s.ActorTenantId, out var timeoutMinutes))
            {
                var cfg = await authConfigStore.GetAsync(s.ActorTenantId, ct);
                timeoutMinutes = cfg?.ImpersonationAutoTimeoutMinutes
                    ?? new TenantAuthConfig { TenantId = s.ActorTenantId }.ImpersonationAutoTimeoutMinutes;
                timeoutCache[s.ActorTenantId] = timeoutMinutes;
            }
            dtos.Add(MapSession(s, timeoutMinutes, now));
        }
        return dtos;
    }

    private static ImpersonationSessionDto MapSession(
        ImpersonationSession s, int autoTimeoutMinutes, DateTimeOffset now)
    {
        int? timeRemainingSeconds = null;
        DateTimeOffset? expiresAt = null;
        if (s.Status == ImpersonationSessionStatus.Active && autoTimeoutMinutes > 0)
        {
            expiresAt = s.StartedAt + TimeSpan.FromMinutes(autoTimeoutMinutes);
            var remaining = expiresAt.Value - now;
            timeRemainingSeconds = remaining.TotalSeconds <= 0 ? 0 : (int)remaining.TotalSeconds;
        }

        return new ImpersonationSessionDto(
            Id: s.Id,
            ActorUserId: s.ActorUserId,
            ActorTenantId: s.ActorTenantId,
            TargetUserId: s.TargetUserId,
            TargetTenantId: s.TargetTenantId,
            Reason: s.Reason,
            ReadOnly: s.ReadOnly,
            StartedAt: s.StartedAt,
            EndedAt: s.EndedAt,
            Status: s.Status.ToString(),
            CloseReason: s.CloseReason,
            TimeRemainingSeconds: timeRemainingSeconds,
            // R5.2 PB.2 — absolute deadline lets the React countdown anchor on
            // a stable timestamp instead of drifting per-render off
            // `timeRemainingSeconds + nowClient`. `null` whenever the tenant
            // disabled auto-timeout (autoTimeoutMinutes == 0).
            ExpiresAt: expiresAt);
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record ImpersonateRequest(
    string TargetTenantId,
    bool ReadOnly = false,
    string? Reason = null);

internal sealed record ImpersonateResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string TargetTenantId,
    string TargetTenantName,
    bool ReadOnly,
    string SessionId);

// R5.2 PB.2 — admin session-management DTOs.

/// <summary>Wire shape for the active + history list endpoints.</summary>
internal sealed record ImpersonationSessionDto(
    string Id,
    string ActorUserId,
    string ActorTenantId,
    string? TargetUserId,
    string TargetTenantId,
    string? Reason,
    bool ReadOnly,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    string? CloseReason,
    int? TimeRemainingSeconds,
    DateTimeOffset? ExpiresAt);

internal sealed record RevokeImpersonationSessionRequest(string? Reason = null);
