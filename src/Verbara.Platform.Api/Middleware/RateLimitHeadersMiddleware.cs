using System.Globalization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Adds <c>X-RateLimit-*</c> headers stating the tenant's tier limit (Standard = 300 req/min) when a tenant is
/// resolved. <b>Not registered in the pipeline:</b> no attached limiter enforces the tier limit, so emitting it
/// would advertise a ceiling that does not exist (and <c>0</c> for an Unlimited tenant). The change
/// wire-api-rate-limiting-enforcement registers it once a tier limiter runs, and adds an <c>unlimited</c> value.
/// Before that it must stop reading <c>Items["TenantId"]</c>: that value is never verified (a JWT caller keeps the
/// header or subdomain tenant, and a 401/403 does not clear it), so registered as is this would disclose the tier
/// of any tenant a caller names, anonymous callers included.
/// </summary>
internal sealed class RateLimitHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public RateLimitHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Headers are written when the response starts: by the time next returns, the endpoint has usually
        // written its body and the headers are read-only. The tenant is read at that point too, so a tenant the
        // API-key handler or a header-less JWT writes later is seen (the value is still unverified — see above).
        context.Response.OnStarting(static state => WriteHeaders((HttpContext)state), context);
        return _next(context);
    }

    private static Task WriteHeaders(HttpContext context)
    {
        // Only add headers when a tenant is resolved (Items["TenantId"] is a boxed TenantId).
        if (!RateLimitPartitionKeys.TryGetResolvedTenant(context, out var resolved))
            return Task.CompletedTask;

        var tenantId = resolved.Value;
        var tier = context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId) ?? RateLimitTier.Standard;
        var limit = tier.GetPermitLimit();

        // Reset = start of next 1-minute window
        var resetEpoch = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();

        context.Response.Headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Tenant"] = tenantId;
        return Task.CompletedTask;
    }
}
