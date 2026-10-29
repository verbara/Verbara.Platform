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

        if (IsLicensed(feature))
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

    /// <summary>
    /// The four features PDR-0003 §E keeps working without a time limit once a licence lapses
    /// (multi-tenancy, routing, realtime and clustering, which voice and queues depend on). They
    /// pass on the signed feature bit and are never put through the licence guard.
    /// </summary>
    internal const LicenseFeature NeverGuardedFeatures =
        LicenseFeature.Cluster | LicenseFeature.MultiTenant | LicenseFeature.Routing | LicenseFeature.Realtime;

    /// <summary>
    /// Decides whether the loaded licence opens <paramref name="feature"/> (one bit, or several that
    /// must all hold, e.g. <c>AdvancedTypification | TypificationAi</c>). The feature bits alone never
    /// decide, because the tracker keeps the bits of a licence that failed validation:
    /// <list type="bullet">
    ///   <item><description>A result that does not come from an authentic, signed licence
    ///   (<see cref="LicenseValidationResult.Invalid"/>, which is also "no licence loaded", or any
    ///   value this code does not know) opens nothing.</description></item>
    ///   <item><description>A signed licence in date (<see cref="LicenseValidationResult.Valid"/>,
    ///   <see cref="LicenseValidationResult.GracePeriod"/>, and
    ///   <see cref="LicenseValidationResult.MissingFeature"/>, which the startup check derives only
    ///   from those two) opens what it carries.</description></item>
    ///   <item><description>A signed licence that is not current
    ///   (<see cref="LicenseValidationResult.Expired"/>,
    ///   <see cref="LicenseValidationResult.UnauthorizedImage"/>) keeps
    ///   <see cref="NeverGuardedFeatures"/> on the bit, and lets every other feature through only
    ///   while <see cref="ILicenseGuard"/> allows it. The guard owns the grace window, so the HTTP
    ///   surface and the Pro engines that call the guard switch off together.</description></item>
    /// </list>
    /// While the licence is Valid or in GracePeriod the guard is still asked about the guarded bits,
    /// so it records them as seen valid and its grace window starts from real use of this surface.
    /// </summary>
    private bool IsLicensed(LicenseFeature feature)
    {
        var result = _licenseStatus.LastResult;
        if (!LicenceAuthenticity.IsAuthentic(result) || !_licenseStatus.LicensedFeatures.HasFlag(feature))
            return false;

        var guarded = feature & ~NeverGuardedFeatures;
        if (guarded == LicenseFeature.None || result == LicenseValidationResult.MissingFeature)
            return true;

        var allowed = true;
        for (var remaining = (int)guarded; remaining != 0; remaining &= remaining - 1)
        {
            // Every bit is asked, even after a denial, so each one is recorded while current. A bit
            // that is not a LicenseFeature member cannot be put to the guard, so it fails closed.
            var bit = (LicenseFeature)(remaining & -remaining);
            allowed &= Enum.IsDefined(bit) && _licenseGuard.CanExecute(bit).Allowed;
        }

        return result is LicenseValidationResult.Valid or LicenseValidationResult.GracePeriod || allowed;
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "License gate: feature '{Feature}' not licensed — blocking {Path}")]
    private static partial void LogFeatureBlocked(
        ILogger logger, LicenseFeature feature, PathString path);
}
