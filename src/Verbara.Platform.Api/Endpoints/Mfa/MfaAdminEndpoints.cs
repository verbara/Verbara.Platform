using System.Security.Claims;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Mfa;

internal static class MfaAdminEndpoints
{
    /// <summary>Authorization policy name for the MFA admin surface.</summary>
    /// <remarks>
    /// Wired in <c>Program.cs</c> via <c>AddPolicy("MfaAdminGate", p =&gt;
    /// p.AddRequirements(new PlatformAdminRequirement("security.mfa.admin")))</c>.
    /// PlatformAdminRequirement combines host/partner-tenant gating with the
    /// permission check so the surface is double-locked.
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

    private static async Task<IResult> ListUsers(
        HttpContext context,
        [FromServices] IMfaAdminService service,
        string? status = null,
        string? tenant = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        _ = context; // signature parity with other admin endpoints
        var filter = new MfaUserListFilter
        {
            Status = status,
            TenantId = tenant,
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
        [FromQuery] string? targetTenant,
        CancellationToken ct)
    {
        var actor = ResolveActor(context);
        var tenantId = ResolveTargetTenant(actor.TenantId, targetTenant);

        var ok = await service.ResetMfaAsync(tenantId, EntityId.From(id), ct);
        if (!ok)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId,
            category: "admin",
            action: "mfa.admin.reset",
            severity: "warning",
            actorId: actor.UserId,
            actorType: "user",
            targetId: id,
            targetType: "user",
            metadata: BuildMetadata(context, actor.TenantId.Value, tenantId.Value),
            ct: ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSessions(
        string id,
        HttpContext context,
        [FromServices] IMfaAdminService service,
        [FromServices] IAuditService audit,
        [FromQuery] string? targetTenant,
        CancellationToken ct)
    {
        var actor = ResolveActor(context);
        var tenantId = ResolveTargetTenant(actor.TenantId, targetTenant);

        var revokedCount = await service.RevokeSessionsAsync(tenantId, EntityId.From(id), ct);
        if (revokedCount < 0)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId,
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
                tenantId.Value,
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

    private static TenantId ResolveTargetTenant(TenantId actorTenant, string? overrideTenant) =>
        string.IsNullOrWhiteSpace(overrideTenant)
            ? actorTenant
            : new TenantId(overrideTenant);

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
