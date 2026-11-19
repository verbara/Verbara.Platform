using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// R5.2 PC.1 — admin retention policy surface. Exposes the existing
/// <c>Pro.Storage.Common.Retention</c> infrastructure (per-target overview, the configured
/// purge mode read-only, manual run-now) behind the <c>system:retention:view</c> /
/// <c>system:retention:manage</c> permissions. The scheduled purge mode is deployment
/// configuration (<c>Retention:DryRun</c>, Platform/ADR-0052); no request changes it.
/// </summary>
/// <remarks>
/// Wired in <c>Program.cs</c> via:
/// <code>
/// options.AddPolicy(RetentionAdminEndpoints.ReadPolicy,
///     p => p.AddRequirements(new PlatformAdminRequirement(PlatformAdminPermissions.RetentionView)));
/// options.AddPolicy(RetentionAdminEndpoints.ManagePolicy,
///     p => p.AddRequirements(new PlatformAdminRequirement(PlatformAdminPermissions.RetentionManage)));
/// </code>
/// Both policies double-lock the surface: PlatformAdmin scope + an explicit RBAC
/// permission, mirroring the PA.1 / PB.1 / PB.2 pattern. ADR-0037 replaced the
/// earlier <c>retention.read</c> / <c>retention.manage</c> ids, which were granted
/// to the admin templates but never catalogued and so never resolvable.
/// </remarks>
public static class RetentionAdminEndpoints
{
    public const string ReadPolicy = "RetentionReadGate";
    public const string ManagePolicy = "RetentionManageGate";

    public static void MapRetentionAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/retention");

        group.MapGet("/targets", ListTargets).RequireAuthorization(ReadPolicy);
        group.MapGet("/config", GetConfig).RequireAuthorization(ReadPolicy);
        group.MapPost("/run-now", RunNow).RequireAuthorization(ManagePolicy);
    }

    private static IResult ListTargets([FromServices] IRetentionAdminService service)
    {
        var targets = service.ListTargets();
        return Results.Ok(targets);
    }

    private static IResult GetConfig([FromServices] IRetentionAdminService service)
    {
        var cfg = service.GetConfig();
        return Results.Ok(cfg);
    }

    private static async Task<IResult> RunNow(
        HttpContext context,
        [FromServices] IRetentionAdminService service,
        [FromServices] IAuditService audit,
        bool? dryRun = null,
        string? target = null,
        CancellationToken ct = default)
    {
        // Default dry-run posture is the safer choice: callers must explicitly
        // pass ?dryRun=false to actually purge data. The manual run states its own
        // mode, independently of the configured scheduled mode (Retention:DryRun).
        var effectiveDryRun = dryRun ?? true;

        var result = await service.RunNowAsync(effectiveDryRun, target, ct);

        var actor = ResolveActor(context);
        await audit.RecordAsync(
            actor.TenantId,
            category: "admin",
            action: "retention.manual_triggered",
            severity: effectiveDryRun ? "info" : "warning",
            actorId: actor.UserId,
            actorType: "user",
            targetId: target ?? "*",
            targetType: "retention_target",
            metadata: BuildMetadata(
                context,
                actor.TenantId.Value,
                ("dry_run", effectiveDryRun ? "true" : "false"),
                ("target", target ?? "*"),
                ("targets_processed", result.Targets.Count.ToString(CultureInfo.InvariantCulture)),
                ("total_rows", result.Targets.Sum(t => t.RowsPurged).ToString(CultureInfo.InvariantCulture))),
            ct: ct);

        return Results.Ok(result);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

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

    private static Dictionary<string, string> BuildMetadata(
        HttpContext context,
        string actorTenantId,
        params (string Key, string Value)[] extras)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actor_tenant_id"] = actorTenantId,
            ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            ["endpoint"] = context.Request.Path.Value ?? "",
        };
        foreach (var (key, value) in extras)
            dict[key] = value;
        return dict;
    }
}
