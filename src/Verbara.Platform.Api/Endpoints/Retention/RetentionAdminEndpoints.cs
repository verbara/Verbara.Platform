using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// R5.2 PC.1 — admin retention policy surface. Exposes the existing
/// <c>Pro.Storage.Common.Retention</c> infrastructure (DryRun toggle, per-target
/// overview, manual run-now) behind the <c>system:retention:view</c> /
/// <c>system:retention:manage</c> permissions.
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
        group.MapPatch("/config", PatchConfig).RequireAuthorization(ManagePolicy);
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
        // pass ?dryRun=false to actually purge data. This mirrors the
        // RetentionOptions.DryRun = true safe default.
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

    private static async Task<IResult> PatchConfig(
        HttpContext context,
        [FromServices] IRetentionAdminService service,
        [FromServices] IAuditService audit,
        [FromBody] RetentionConfigPatchDto body,
        CancellationToken ct)
    {
        if (body is null)
            return Results.BadRequest(new { error = "Body required." });

        var actor = ResolveActor(context);

        // DryRun toggle (only mutable property in v1).
        if (body.DryRun is bool newDryRun)
        {
            var previous = service.SetDryRun(newDryRun);
            if (previous != newDryRun)
            {
                await audit.RecordAsync(
                    actor.TenantId,
                    category: "admin",
                    action: "retention.dryrun_toggled",
                    severity: newDryRun ? "info" : "warning",
                    actorId: actor.UserId,
                    actorType: "user",
                    targetId: "global",
                    targetType: "retention_config",
                    metadata: BuildMetadata(
                        context,
                        actor.TenantId.Value,
                        ("previous_dry_run", previous ? "true" : "false"),
                        ("new_dry_run", newDryRun ? "true" : "false")),
                    ct: ct);
            }
        }

        // Generic config-changed audit when ANY field was patched (covers
        // future expansion; in v1 only DryRun mutates so this overlaps with
        // the dryrun_toggled entry — intentional, the dryrun_toggled is the
        // "interesting" one for compliance review).
        if (body.DryRun is not null)
        {
            await audit.RecordAsync(
                actor.TenantId,
                category: "admin",
                action: "retention.config_changed",
                severity: "info",
                actorId: actor.UserId,
                actorType: "user",
                targetId: "global",
                targetType: "retention_config",
                metadata: BuildMetadata(
                    context,
                    actor.TenantId.Value,
                    ("fields_changed", "dry_run")),
                ct: ct);
        }

        return Results.Ok(service.GetConfig());
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
