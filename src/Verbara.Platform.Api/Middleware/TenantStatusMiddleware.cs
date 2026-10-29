using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Middleware;

internal sealed class TenantStatusMiddleware
{
    private readonly RequestDelegate _next;

    public TenantStatusMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The one route served whatever the tenant's status (ending an impersonation, design D1): read from the
        // routed endpoint, never matched by path. Neither refused nor used to populate the tenant caches.
        if (IsServedWhateverTenantStatus(context))
        {
            await _next(context);
            return;
        }

        if (!context.Items.TryGetValue("TenantId", out var tenantIdObj) || tenantIdObj is not TenantId tenantId)
        {
            await _next(context);
            return;
        }

        var tenantStore = context.RequestServices.GetRequiredService<ITenantStore>();
        var tenant = await tenantStore.GetAsync(tenantId.Value, context.RequestAborted);

        if (tenant is null)
        {
            await _next(context);
            return;
        }

        switch (tenant.Status)
        {
            case TenantStatus.Suspended:
                await WriteError(context, 403, AnswersWithCode(context)
                    ? TenantStatusRefusals.Suspended
                    : "This tenant account has been suspended. Contact your administrator.");
                return;

            case TenantStatus.PendingDeletion:
                await WriteError(context, 403, AnswersWithCode(context)
                    ? TenantStatusRefusals.PendingDeletion
                    : "This tenant account is pending deletion due to prolonged non-payment. Contact your administrator immediately.");
                return;

            case TenantStatus.Deleted:
                await WriteError(context, 404, "Not found");
                return;

            case TenantStatus.Warning:
            case TenantStatus.Degraded:
                context.Response.Headers["X-Tenant-Warning"] = "payment_overdue";
                context.Items["Tenant"] = tenant;
                await PopulateCaches(context, tenant);
                await _next(context);
                return;

            default: // Active or any unknown
                context.Items["Tenant"] = tenant;
                await PopulateCaches(context, tenant);
                await _next(context);
                return;
        }
    }

    /// <summary>
    /// True when the matched endpoint opted into the coded refusal
    /// (<see cref="TenantStatusRefusalEndpointExtensions.AnswersTenantRefusalWithCode"/>); the routing middleware
    /// runs before this one, so the endpoint is already selected.
    /// </summary>
    private static bool AnswersWithCode(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<TenantRefusalCodeMetadata>() is not null;

    /// <summary>
    /// True when the matched endpoint carries <see cref="ServedWhateverTenantStatusMetadata"/>
    /// (<see cref="ServedWhateverTenantStatusEndpointExtensions.ServedWhateverTenantStatus"/>).
    /// </summary>
    private static bool IsServedWhateverTenantStatus(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ServedWhateverTenantStatusMetadata>() is not null;

    private static async Task PopulateCaches(HttpContext context, Tenant tenant)
    {
        var plan = tenant.GetPlan();

        // Rate limit tier: explicit override in Metadata takes priority, otherwise derive from plan
        var tierCache = context.RequestServices.GetService<TenantTierCache>();
        var hasExplicitTier = tenant.Metadata?.ContainsKey("RateLimitTier") == true;
        var tier = hasExplicitTier ? tenant.GetRateLimitTier() : PlanDefinition.GetDefaultTier(plan);
        tierCache?.SetTier(tenant.TenantId, tier);

        // Feature gate cache
        var featureGateCache = context.RequestServices.GetService<FeatureGateCache>();
        if (featureGateCache is null)
            return;

        var effectivePlan = tenant.Status == TenantStatus.Degraded ? TenantPlan.Starter : plan;
        var features = new HashSet<PlanFeature>(PlanDefinition.GetFeatures(effectivePlan));

        // Add-ons (only when not degraded — degraded forces Starter features)
        if (tenant.Status != TenantStatus.Degraded)
        {
            var addOnStore = context.RequestServices.GetService<ITenantAddOnStore>();
            if (addOnStore is not null)
            {
                var addOns = await addOnStore.GetAsync(tenant.TenantId, context.RequestAborted);
                foreach (var addOn in addOns)
                    features.Add(addOn.Feature);
            }
        }

        // Hierarchy ceiling: intersect with parent's plan features
        if (tenant.ParentTenantId is not null)
        {
            var parentTenant = await context.RequestServices.GetRequiredService<ITenantStore>()
                .GetAsync(tenant.ParentTenantId, context.RequestAborted);
            if (parentTenant is not null)
            {
                var parentFeatures = PlanDefinition.GetFeatures(parentTenant.GetPlan());
                features.IntersectWith(parentFeatures);
            }
        }

        featureGateCache.Set(tenant.TenantId, new ResolvedFeatures(
            effectivePlan,
            features.AsReadOnly(),
            PlanDefinition.GetMaxChannels(effectivePlan),
            PlanDefinition.GetAuditRetentionDays(effectivePlan),
            PlanDefinition.GetMaxWebhookSubscriptions(effectivePlan),
            PlanDefinition.GetMaxScheduledReports(effectivePlan)));
    }

    private static async Task WriteError(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
            new ErrorResponse(detail),
            ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
    }
}
