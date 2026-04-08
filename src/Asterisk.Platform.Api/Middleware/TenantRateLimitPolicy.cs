using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.RateLimiting;

namespace Asterisk.Platform.Api.Middleware;

internal static class TenantRateLimitPolicy
{
    public static void ConfigureRateLimiting(RateLimiterOptions options)
    {
        options.RejectionStatusCode = 429;

        // Global safety net
        options.AddSlidingWindowLimiter("global-safety", o =>
        {
            o.Window = TimeSpan.FromMinutes(1);
            o.SegmentsPerWindow = 6;
            o.PermitLimit = 3000;
        });

        // Per-tenant partitioned policy
        options.AddPolicy("per-tenant", context =>
        {
            var tenantId = context.Items.TryGetValue("TenantId", out var val) && val is string s
                ? s : "__global__";

            var tier = tenantId == "__global__"
                ? RateLimitTier.Unlimited
                : context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId) ?? RateLimitTier.Standard;

            if (tier.IsUnlimited())
                return RateLimitPartition.GetNoLimiter(tenantId);

            return RateLimitPartition.GetSlidingWindowLimiter(tenantId, _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                PermitLimit = tier.GetPermitLimit(),
                AutoReplenishment = true,
            });
        });

        // Custom 429 response
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            context.HttpContext.Response.ContentType = "application/json";

            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
                ? (int)retryAfterValue.TotalSeconds : 30;

            context.HttpContext.Response.Headers["Retry-After"] = retryAfter.ToString(CultureInfo.InvariantCulture);

            var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Type = "rate_limit_exceeded",
                Title = "Too Many Requests",
                Status = 429,
                Detail = "Tenant rate limit exceeded",
            };
            problem.Extensions["retryAfter"] = retryAfter;
            await context.HttpContext.Response.WriteAsync(
                JsonSerializer.Serialize(problem, ApiJsonContext.Default.ProblemDetails), ct);
        };
    }
}
