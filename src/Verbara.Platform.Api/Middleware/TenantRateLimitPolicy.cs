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
    /// every tenant, regardless of rate-limit tier — cost control is universal). Deliberately modest.
    /// The partition is keyed per tenant via the request (see <see cref="ResolveTenantKey"/>), so each
    /// tenant gets its own 30/min bucket — not a single shared global cap.
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

        // Per-tenant partitioned policy.
        // PRE-EXISTING FLAW (out of scope for E3): UseRateLimiter() runs BEFORE
        // TenantResolutionMiddleware (the sole writer of Items["TenantId"]), so at this
        // pipeline position Items["TenantId"] is UNSET and every request collapses to the
        // "__global__" key → Unlimited tier → NoLimiter (this policy never throttles anyone).
        // The "llm" policy below works around this by resolving the tenant key directly from
        // the request (header). The proper fix for "per-tenant" is to move UseRateLimiter()
        // AFTER tenant resolution — a cross-cutting pipeline reorder affecting every
        // rate-limited route, tracked as a separate follow-up.
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
        // tier, so even an Unlimited-tier tenant is throttled here.
        //
        // The partition key is resolved DIRECTLY from the request (see ResolveTenantKey) rather
        // than from Items["TenantId"]: UseRateLimiter() runs before TenantResolutionMiddleware,
        // so Items["TenantId"] is not yet populated at this pipeline position. Reading the
        // X-Tenant-Id header (the primary tenant signal the Web sends) gives each tenant its own
        // 30/min bucket instead of collapsing every tenant into a single global 5/min partition.
        options.AddPolicy("llm", context =>
        {
            var tenantId = ResolveTenantKey(context);

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

    /// <summary>
    /// Resolves the rate-limit partition key for the <c>llm</c> policy WITHOUT depending on
    /// <c>Items["TenantId"]</c> being populated (it is not, at the <c>UseRateLimiter()</c> pipeline
    /// position — that runs before <see cref="TenantResolutionMiddleware"/>). Resolution order:
    /// <list type="number">
    ///   <item><c>Items["TenantId"]</c> as string — honored first in case a future middleware-order
    ///   change populates it early.</item>
    ///   <item>the <c>X-Tenant-Id</c> request header — the primary tenant signal the Web sends.</item>
    ///   <item><c>"__global__"</c> — the bounded pre-auth / no-tenant fallback.</item>
    /// </list>
    /// </summary>
    private static string ResolveTenantKey(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is string s && !string.IsNullOrWhiteSpace(s))
            return s;

        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var header))
        {
            var headerValue = header.ToString();
            if (!string.IsNullOrWhiteSpace(headerValue))
                return headerValue;
        }

        return "__global__";
    }
}
