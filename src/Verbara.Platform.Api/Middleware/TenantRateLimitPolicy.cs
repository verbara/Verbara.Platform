using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.RateLimiting;

namespace Verbara.Platform.Api.Middleware;

internal static class TenantRateLimitPolicy
{
    /// <summary>
    /// E3 — per-tenant permit limit for the dedicated <c>llm</c> policy (sliding 1-minute window).
    /// LLM calls are expensive at EVERY tier, so this policy is NOT tier-bypassed (it applies to
    /// every tenant, including the Unlimited tier — cost control is universal). Deliberately modest.
    /// </summary>
    internal const int LlmPermitLimit = 30;

    /// <summary>Window length for the <c>llm</c> sliding-window limiter.</summary>
    internal static readonly TimeSpan LlmWindow = TimeSpan.FromMinutes(1);

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

        // E3 — dedicated per-tenant LLM rate-limit policy. Applied to the expensive
        // AI-suggestion route ONLY (in addition to the generic per-tenant limiter). Unlike
        // "per-tenant" this policy is NOT tier-bypassed: an LLM call costs real money at every
        // tier, so even an Unlimited-tier tenant is throttled here. Partition key matches
        // "per-tenant" (the resolved TenantId, or "__global__" pre-auth) so the budget is per-tenant.
        options.AddPolicy("llm", context =>
        {
            var tenantId = context.Items.TryGetValue("TenantId", out var val) && val is string s
                ? s : "__global__";

            // Pre-auth / no-tenant traffic gets a small partition of its own (it never reaches
            // the authenticated suggestion route in practice, but keep it bounded).
            var permitLimit = tenantId == "__global__" ? 5 : LlmPermitLimit;

            return RateLimitPartition.GetSlidingWindowLimiter(tenantId, _ => new SlidingWindowRateLimiterOptions
            {
                Window = LlmWindow,
                SegmentsPerWindow = 6,
                PermitLimit = permitLimit,
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
