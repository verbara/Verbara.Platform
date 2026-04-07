using System.Text.Json;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Middleware;

internal sealed class TenantStatusMiddleware
{
    private readonly RequestDelegate _next;

    public TenantStatusMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
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
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body,
                    new ErrorResponse("This tenant account has been suspended. Contact your administrator."),
                    ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
                return;

            case TenantStatus.Deleted:
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body,
                    new ErrorResponse("Not found"),
                    ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
                return;

            default:
                context.Items["Tenant"] = tenant;
                var tierCache = context.RequestServices.GetService<TenantTierCache>();
                tierCache?.SetTier(tenantId.Value, tenant.GetRateLimitTier());
                await _next(context);
                return;
        }
    }
}
