using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Middleware;

internal sealed partial class LicenseGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LicenseGateMiddleware> _logger;
    private readonly ILicenseStatus _licenseStatus;
    private readonly ILicenseGuard _licenseGuard;

    public LicenseGateMiddleware(
        RequestDelegate next,
        ILogger<LicenseGateMiddleware> logger,
        ILicenseStatus licenseStatus,
        ILicenseGuard licenseGuard)
    {
        _next = next;
        _logger = logger;
        _licenseStatus = licenseStatus;
        _licenseGuard = licenseGuard;
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

        if (isLicensed)
        {
            await _next(context);
            return;
        }

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

        var guardResult = _licenseGuard.CanExecute(feature);

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

    [LoggerMessage(Level = LogLevel.Error,
        Message = "License gate: feature '{Feature}' not licensed — blocking {Path}")]
    private static partial void LogFeatureBlocked(
        ILogger logger, LicenseFeature feature, PathString path);
}
