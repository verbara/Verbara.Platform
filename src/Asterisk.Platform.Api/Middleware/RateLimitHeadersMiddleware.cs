using System.Globalization;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Middleware;

/// <summary>
/// Adds X-RateLimit-* informational headers to responses when a tenant is resolved.
/// The limit value reflects the tenant's assigned tier (Standard = 300 req/min by default).
/// </summary>
internal sealed class RateLimitHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public RateLimitHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        // Only add headers when a tenant is resolved
        if (!context.Items.TryGetValue("TenantId", out var tenantIdObj) || tenantIdObj is not string tenantId)
            return;

        var tier = context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId) ?? RateLimitTier.Standard;
        var limit = tier.GetPermitLimit();

        // Reset = start of next 1-minute window
        var resetEpoch = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();

        context.Response.Headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Tenant"] = tenantId;
    }
}
