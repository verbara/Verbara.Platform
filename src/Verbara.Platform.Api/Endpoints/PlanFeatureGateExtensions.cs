using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Endpoints;

internal static class PlanFeatureGateExtensions
{
    public static RouteGroupBuilder RequirePlanFeature(this RouteGroupBuilder group, PlanFeature feature)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var tenantId = httpContext.Items.TryGetValue("TenantId", out var tid) && tid is TenantId tenantIdVal ? tenantIdVal.Value : null;

            if (tenantId is null)
                return await next(context);

            // Platform tenant bypasses feature gates
            if (httpContext.Items["Tenant"] is Tenant { Type: TenantType.Platform })
                return await next(context);

            var featureGate = httpContext.RequestServices.GetService<IFeatureGateService>();
            if (featureGate is null || featureGate.IsFeatureEnabled(tenantId, feature))
                return await next(context);

            var plan = httpContext.RequestServices.GetService<FeatureGateCache>()?.Get(tenantId)?.EffectivePlan ?? TenantPlan.Starter;

            return Results.Json(
                new ErrorResponse($"This feature is not available on your current plan ({plan}). Upgrade to access this feature."),
                ApiJsonContext.Default.ErrorResponse,
                statusCode: 403);
        });

        return group;
    }
}
