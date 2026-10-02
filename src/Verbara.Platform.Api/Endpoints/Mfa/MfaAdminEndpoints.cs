using System.Security.Claims;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Mfa;

internal static class MfaAdminEndpoints
{
    /// <summary>Authorization policy name for the MFA admin surface.</summary>
    /// <remarks>
    /// Wired in <c>Program.cs</c> with <c>allowPartnerDelegation: true</c>, which every handler
    /// in this group pays for by running its target tenant through
    /// <see cref="ResolveTargetTenantAsync"/> — a Partner reaches its own subtree and nothing
    /// else. The requirement also carries the <c>system:mfa:manage</c> permission, so the
    /// surface is double-locked (ADR-0037; previously the uncatalogued
    /// <c>security.mfa.admin</c>, which no principal could hold).
    /// </remarks>
    public const string AuthorizationPolicy = "MfaAdminGate";

    public static void MapMfaAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/mfa")
            .RequireAuthorization(AuthorizationPolicy);

        group.MapGet("/users", ListUsers);
        group.MapPost("/users/{id}/reset", ResetMfa);
        group.MapPost("/users/{id}/sessions/revoke", RevokeSessions);
    }

    /// <summary>
    /// Lists MFA enrollment status. Reads the same directory the two POST verbs write to, so it
    /// pays the same toll: the caller-supplied tenant goes through
    /// <see cref="ResolveTargetTenantAsync"/>, and an out-of-hierarchy value is audited and
    /// refused exactly as a reset would be.
    /// </summary>
    /// <remarks>
    /// The unfiltered fan-out is the part worth being careful about. <c>MfaAdminService</c> reads
    /// an absent tenant filter as "every active tenant", which is correct for the Platform host
    /// and is an installation-wide directory dump for anybody else — every user's email, MFA
    /// state and last login, in every tenant. So a non-host caller with no filter is scoped to
    /// its own tenant, and reaches a descendant only by naming it. <c>tenant</c> is kept as the
    /// parameter name because the console sends it (<c>use-mfa-users.ts</c>);
    /// <c>targetTenant</c> is accepted too, for parity with the reset and revoke verbs.
    /// </remarks>
    private static async Task<IResult> ListUsers(
        HttpContext context,
        [FromServices] IMfaAdminService service,
        [FromServices] IAuditService audit,
        [FromServices] ITenantStore tenantStore,
        string? status = null,
        string? tenant = null,
        [FromQuery] string? targetTenant = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var actor = ResolveActor(context);
        var requested = string.IsNullOrWhiteSpace(targetTenant) ? tenant : targetTenant;

        string? scopedTenantId;
        if (string.IsNullOrWhiteSpace(requested))
        {
            var callerTenant = await tenantStore.GetAsync(actor.TenantId.Value, ct);
            scopedTenantId = callerTenant?.Type == TenantType.Platform ? null : actor.TenantId.Value;
        }
        else
        {
            var (resolved, error) = await ResolveTargetTenantAsync(
                actor.TenantId, requested, tenantStore, audit, actor.UserId, context, "mfa.admin.list", ct);
            if (error is not null)
                return error;

            scopedTenantId = resolved!.Value.Value;
        }

        var filter = new MfaUserListFilter
        {
            Status = status,
            TenantId = scopedTenantId,
            Page = page,
            PageSize = pageSize,
        };
        var result = await service.ListAsync(filter, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> ResetMfa(
        string id,
        HttpContext context,
        [FromServices] IMfaAdminService service,
        [FromServices] IAuditService audit,
        [FromServices] ITenantStore tenantStore,
        [FromQuery] string? targetTenant,
        CancellationToken ct)
    {
        var actor = ResolveActor(context);
        var (tenantId, error) = await ResolveTargetTenantAsync(
            actor.TenantId, targetTenant, tenantStore, audit, actor.UserId, context, "mfa.admin.reset", ct);
        if (error is not null)
            return error;

        var ok = await service.ResetMfaAsync(tenantId!.Value, EntityId.From(id), ct);
        if (!ok)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId.Value,
            category: "admin",
            action: "mfa.admin.reset",
            severity: "warning",
            actorId: actor.UserId,
            actorType: "user",
            targetId: id,
            targetType: "user",
            metadata: BuildMetadata(context, actor.TenantId.Value, tenantId.Value.Value),
            ct: ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSessions(
        string id,
        HttpContext context,
        [FromServices] IMfaAdminService service,
        [FromServices] IAuditService audit,
        [FromServices] ITenantStore tenantStore,
        [FromQuery] string? targetTenant,
        CancellationToken ct)
    {
        var actor = ResolveActor(context);
        var (tenantId, error) = await ResolveTargetTenantAsync(
            actor.TenantId, targetTenant, tenantStore, audit, actor.UserId, context, "mfa.admin.sessions_revoked", ct);
        if (error is not null)
            return error;

        var revokedCount = await service.RevokeSessionsAsync(tenantId!.Value, EntityId.From(id), ct);
        if (revokedCount < 0)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId.Value,
            category: "admin",
            action: "mfa.admin.sessions_revoked",
            severity: "warning",
            actorId: actor.UserId,
            actorType: "user",
            targetId: id,
            targetType: "user",
            metadata: BuildMetadata(
                context,
                actor.TenantId.Value,
                tenantId.Value.Value,
                ("revoked_count", revokedCount.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ct: ct);

        return Results.NoContent();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (TenantId TenantId, string UserId) ResolveActor(HttpContext context)
    {
        var tenantClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value
            ?? "platform";
        var userClaim = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? "system";
        return (new TenantId(tenantClaim), userClaim);
    }

    /// <summary>
    /// MFA-001 fix (PREPUB-2026-05-09): the legacy synchronous resolver trusted
    /// any caller-supplied <paramref name="overrideTenant"/>. Mirror the
    /// impersonation-hierarchy pattern from
    /// <see cref="ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync"/>:
    /// allow iff (a) override is null/whitespace, (b) override equals actor's
    /// own tenant, (c) the actor sits in a Platform tenant, or (d) the override
    /// is a descendant of the actor's tenant in the parent chain. Otherwise
    /// emit <see cref="AuthEventTypes.MfaPrivilegeEscalationAttempted"/> on the
    /// caller's audit log and reject with 403.
    /// </summary>
    private static async Task<(TenantId? Tenant, IResult? Error)> ResolveTargetTenantAsync(
        TenantId actorTenant,
        string? overrideTenant,
        ITenantStore tenantStore,
        IAuditService audit,
        string actorUserId,
        HttpContext context,
        string attemptedAction,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(overrideTenant))
            return (actorTenant, null);
        if (string.Equals(actorTenant.Value, overrideTenant, StringComparison.Ordinal))
            return (actorTenant, null);

        var callerTenant = await tenantStore.GetAsync(actorTenant.Value, ct);
        if (callerTenant?.Type == TenantType.Platform)
            return (new TenantId(overrideTenant), null);

        var inHierarchy = await ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync(
            tenantStore, actorTenant.Value, overrideTenant, ct);
        if (inHierarchy)
            return (new TenantId(overrideTenant), null);

        // Audit the attempt on the CALLER tenant before rejecting — this is the
        // signal a Partner Admin tried to escalate into a foreign hierarchy.
        await audit.RecordAsync(
            actorTenant,
            category: "admin",
            action: AuthEventTypes.MfaPrivilegeEscalationAttempted,
            severity: "error",
            actorId: actorUserId,
            actorType: "user",
            targetId: overrideTenant,
            targetType: "tenant",
            metadata: BuildMetadata(
                context,
                actorTenant.Value,
                overrideTenant,
                ("attempted_action", attemptedAction)),
            ct: ct);

        return (null, Results.Problem(
            title: "MFA admin not authorized",
            detail: "Target tenant is not in your tenant hierarchy.",
            statusCode: StatusCodes.Status403Forbidden));
    }

    private static Dictionary<string, string> BuildMetadata(
        HttpContext context,
        string actorTenantId,
        string targetTenantId,
        params (string Key, string Value)[] extras)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actor_tenant_id"] = actorTenantId,
            ["target_tenant_id"] = targetTenantId,
            ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            ["endpoint"] = context.Request.Path.Value ?? "",
        };
        foreach (var (key, value) in extras)
            dict[key] = value;
        return dict;
    }
}
