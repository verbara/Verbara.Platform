using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Enforces the per-tenant IP allowlist on every authenticated request.
/// Skipped for endpoints marked [AllowAnonymous] (covers login, refresh,
/// health, OIDC callback). Soft-gated: if the tenant's plan does not
/// include PlanFeature.IpAllowlist, the middleware is silently inert.
/// PlatformAdmin role bypasses the check (rescue valve, audited).
/// See docs/specs/2026-05-02-ip-allowlist-design.md §3.4 + §4.
/// </summary>
internal sealed class IpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IpAllowlistMiddleware> _logger;

    public IpAllowlistMiddleware(RequestDelegate next, ILogger<IpAllowlistMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantIpAllowlistStore store,
        ITenantAuthConfigStore authConfigStore,
        IIpAllowlistEvaluator evaluator,
        IFeatureGateService featureGate,
        IAuditService audit)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await _next(context);
            return;
        }

        var tenantId = context.User.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrEmpty(tenantId))
        {
            await _next(context);
            return;
        }

        if (!featureGate.IsFeatureEnabled(tenantId, PlanFeature.IpAllowlist))
        {
            await _next(context);
            return;
        }

        var role = context.User.FindFirst(ClaimTypes.Role)?.Value;
        var clientIp = context.Connection.RemoteIpAddress;

        if (string.Equals(role, "PlatformAdmin", StringComparison.Ordinal))
        {
            await audit.RecordAsync(
                tenantId: new TenantId(tenantId),
                category: "auth",
                action: "auth.ip_allowlist.bypass",
                severity: "info",
                actorId: context.User.Identity?.Name ?? "unknown",
                actorType: "user",
                metadata: new Dictionary<string, string>
                {
                    ["ip"] = clientIp?.ToString() ?? "unknown",
                    ["path"] = context.Request.Path,
                },
                ct: context.RequestAborted);

            await _next(context);
            return;
        }

        var config = await authConfigStore.GetAsync(tenantId, context.RequestAborted);
        if (config is null || !config.IpAllowlistEnabled)
        {
            await _next(context);
            return;
        }

        var entries = await store.ListAsync(tenantId, context.RequestAborted);

        if (clientIp is null || !evaluator.IsAllowed(clientIp, entries))
        {
            await audit.RecordAsync(
                tenantId: new TenantId(tenantId),
                category: "auth",
                action: "auth.ip_allowlist.denied",
                severity: "warning",
                actorId: context.User.Identity?.Name ?? "unknown",
                actorType: "user",
                metadata: new Dictionary<string, string>
                {
                    ["ip"] = clientIp?.ToString() ?? "unknown",
                    ["path"] = context.Request.Path,
                    ["entry_count"] = entries.Count.ToString(CultureInfo.InvariantCulture),
                },
                ct: context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"code":"ip_allowlist_violation"}""", context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
