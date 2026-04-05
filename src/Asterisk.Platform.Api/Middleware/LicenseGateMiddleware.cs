using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Middleware;

internal sealed partial class LicenseGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LicenseGateMiddleware> _logger;
    private readonly ILicenseStatus _licenseStatus;
    private readonly EnforcementMode _enforcementMode;

    public LicenseGateMiddleware(
        RequestDelegate next,
        ILogger<LicenseGateMiddleware> logger,
        ILicenseStatus licenseStatus,
        IOptions<LicenseOptions> options)
    {
        _next = next;
        _logger = logger;
        _licenseStatus = licenseStatus;
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

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = 403,
            title = "Feature Not Licensed",
            detail = $"The '{feature}' feature is not included in your current license.",
            instance = context.Request.Path.Value,
        }, context.RequestAborted);
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
