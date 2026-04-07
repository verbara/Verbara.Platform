using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Endpoints;

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

            return Results.Json(new
            {
                type = "feature_not_available",
                title = "Feature Not Available",
                detail = $"This feature is not available on your current plan ({plan}). Upgrade to access this feature.",
            }, statusCode: 403);
        });

        return group;
    }
}
