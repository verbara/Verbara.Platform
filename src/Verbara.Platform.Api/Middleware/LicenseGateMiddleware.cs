using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.Extensions.Options;

// Back-compat licensing path: Platform v2.2.0 consumes Pro v2.4.0-pro which marks
// LicenseOptions.EnforcementMode [Obsolete]. We preserve the 3-mode behaviour
// (Disabled / WarnOnly / Enforce) until Platform's lockstep migration with Pro v2.5.0-pro.
// See ADR-0012 + plan ~/.claude/plans/si-refactored-pascal.md Phase D.
#pragma warning disable CS0618 // EnforcementMode

namespace Verbara.Platform.Api.Middleware;

internal sealed partial class LicenseGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LicenseGateMiddleware> _logger;
    private readonly ILicenseStatus _licenseStatus;
    private readonly ILicenseGuard _licenseGuard;
    private readonly EnforcementMode _enforcementMode;

    public LicenseGateMiddleware(
        RequestDelegate next,
        ILogger<LicenseGateMiddleware> logger,
        ILicenseStatus licenseStatus,
        ILicenseGuard licenseGuard,
        IOptions<LicenseOptions> options)
    {
        _next = next;
        _logger = logger;
        _licenseStatus = licenseStatus;
        _licenseGuard = licenseGuard;
        _enforcementMode = options.Value.EnforcementMode;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<LicenseFeatureMetadata>();

        if (metadata is null)
        {
            await _next(context);
            return;
        }

        var feature = metadata.RequiredFeature;
        var isLicensed = _licenseStatus.LicensedFeatures.HasFlag(feature);

        if (isLicensed || _enforcementMode == EnforcementMode.Disabled)
        {
            await _next(context);
            return;
        }

        if (_enforcementMode == EnforcementMode.WarnOnly)
        {
            LogFeatureUnlicensedWarn(_logger, feature, context.Request.Path);

            context.Response.Headers["X-License-Warning"] =
                $"Feature '{feature}' is not licensed. Access permitted in WarnOnly mode.";

            var audit = context.RequestServices.GetService<IAuditService>();
            if (audit is not null)
            {
                await audit.RecordAsync(
                    tenantId: new TenantId("system"),
                    category: "license",
                    action: "license.gate.warn",
                    severity: "warning",
                    actorId: context.User.Identity?.Name ?? "anonymous",
                    actorType: "user",
                    targetId: feature.ToString(),
                    targetType: "LicenseFeature",
                    metadata: new Dictionary<string, string>
                    {
                        ["path"] = context.Request.Path,
                        ["method"] = context.Request.Method,
                    },
                    ct: context.RequestAborted);
            }

            await _next(context);
            return;
        }

        // EnforcementMode.Enforce
        LogFeatureBlocked(_logger, feature, context.Request.Path);

        var auditEnforce = context.RequestServices.GetService<IAuditService>();
        if (auditEnforce is not null)
        {
            await auditEnforce.RecordAsync(
                tenantId: new TenantId("system"),
                category: "license",
                action: "license.gate.blocked",
                severity: "error",
                actorId: context.User.Identity?.Name ?? "anonymous",
                actorType: "user",
                targetId: feature.ToString(),
                targetType: "LicenseFeature",
                metadata: new Dictionary<string, string>
                {
                    ["path"] = context.Request.Path,
                    ["method"] = context.Request.Method,
                },
                ct: context.RequestAborted);
        }

        // Pro v2.4.0-pro — consult LicenseGuard so we surface the enriched URLs
        // (TierRequired / TrialUrl / UpgradeUrl / ContactSalesUrl) populated by
        // LicenseGuard.Enrich based on the resolved LicenseBlockReason.
        var guardResult = _licenseGuard.CanExecute(feature);

        // HTTP 402 Payment Required — RFC 9110 reserves this for subscription/payment
        // gates (Stripe-style). 4xx ⇒ doesn't burn SLO error budget / oncall pages;
        // clients don't auto-retry 402. See ADR-0012 + plan.
        context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        context.Response.ContentType = "application/problem+json";
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status402PaymentRequired,
            Title = "Feature Not Licensed",
            Detail = $"The '{feature}' feature is not included in your current license.",
            Instance = context.Request.Path.Value,
            Type = "https://verbara.io/problems/license-required",
        };

        // RFC 9457 extension members — actionable URLs sourced from the enriched
        // LicenseGuardResult. Nullable propagation: omit keys when Pro returns null
        // (e.g. UnauthorizedImage reason omits all URLs intentionally).
        if (guardResult.TierRequired is { } tier)
            problem.Extensions["tier_required"] = tier.ToString();
        if (!string.IsNullOrEmpty(guardResult.TrialUrl))
            problem.Extensions["trial_url"] = guardResult.TrialUrl;
        if (!string.IsNullOrEmpty(guardResult.UpgradeUrl))
            problem.Extensions["upgrade_url"] = guardResult.UpgradeUrl;
        if (!string.IsNullOrEmpty(guardResult.ContactSalesUrl))
            problem.Extensions["contact_sales_url"] = guardResult.ContactSalesUrl;

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, ApiJsonContext.Default.ProblemDetails),
            context.RequestAborted);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "License gate: feature '{Feature}' not licensed — WarnOnly mode allows {Path}")]
    private static partial void LogFeatureUnlicensedWarn(
        ILogger logger, LicenseFeature feature, PathString path);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "License gate: feature '{Feature}' not licensed — blocking {Path}")]
    private static partial void LogFeatureBlocked(
        ILogger logger, LicenseFeature feature, PathString path);
}
