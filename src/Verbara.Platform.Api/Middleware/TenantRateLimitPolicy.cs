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

    /// <summary>
    /// E3 — permit limit for the shared <c>"__global__"</c> fallback partition of the <c>llm</c>
    /// policy. Set equal to <see cref="LlmPermitLimit"/> (30): a header-less authenticated caller
    /// (JWT-<c>tid</c>-only, no <c>X-Tenant-Id</c> header, no tenant subdomain) still cannot be
    /// partitioned per tenant — even though <c>UseRateLimiter()</c> now runs after
    /// <c>TenantResolutionMiddleware</c>, it must stay BEFORE <c>UseAuthentication</c> (throttle
    /// before auth work), and the JWT <c>tid</c> claim is only applied to <c>Items["TenantId"]</c>
    /// during authentication — so that caller lands in this shared bucket. Capping it at the
    /// per-tenant limit (rather than a punitive low value) keeps cost control without starving
    /// that real caller class.
    /// </summary>
    internal const int GlobalFallbackPermitLimit = 30;

    /// <summary>Window length for the <c>llm</c> sliding-window limiter.</summary>
    internal static readonly TimeSpan LlmWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the <c>per-tenant</c> partition key + tier for the current request.
    /// Reads the tenant from <c>Items["TenantId"]</c> (a string, written by
    /// <see cref="TenantResolutionMiddleware"/>); a request whose tenant could not be
    /// resolved lands in the shared <c>"__global__"</c> partition at the Unlimited tier
    /// (NoLimiter). Because <c>UseRateLimiter()</c> now runs AFTER tenant resolution
    /// (see the middleware-order note in <c>Program.cs</c>), <c>Items["TenantId"]</c> is
    /// populated here for every header/subdomain-identified tenant, so each gets its own
    /// partition instead of all collapsing to <c>"__global__"</c>.
    /// </summary>
    internal static (string PartitionKey, RateLimitTier Tier) ResolvePerTenantTarget(HttpContext context)
    {
        var tenantId = context.Items.TryGetValue("TenantId", out var val) && val is string s
            ? s : "__global__";

        var tier = tenantId == "__global__"
            ? RateLimitTier.Unlimited
            : context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId) ?? RateLimitTier.Standard;

        return (tenantId, tier);
    }

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

        // Per-tenant partitioned policy. UseRateLimiter() now runs AFTER
        // TenantResolutionMiddleware (the sole writer of Items["TenantId"]) — see the
        // middleware-order note in Program.cs — so Items["TenantId"] IS populated at this
        // pipeline position for every header/subdomain-identified tenant. Each such tenant
        // gets its own partition (a request with no resolvable tenant still lands in the
        // shared "__global__" → Unlimited → NoLimiter bucket). Partition+tier resolution is
        // extracted into ResolvePerTenantTarget so it can be unit-tested directly.
        options.AddPolicy("per-tenant", context =>
        {
            var (tenantId, tier) = ResolvePerTenantTarget(context);

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
        // ResolveTenantKey reads Items["TenantId"] first (now populated, since UseRateLimiter()
        // runs after TenantResolutionMiddleware) and falls back to the X-Tenant-Id request
        // header, so each tenant gets its own 30/min bucket. A caller with no resolvable tenant
        // shares the bounded "__global__" bucket (GlobalFallbackPermitLimit == LlmPermitLimit).
        options.AddPolicy("llm", context =>
        {
            var tenantId = ResolveTenantKey(context);

            // The "__global__" fallback is NOT only pre-auth traffic: a header-less authenticated
            // caller (JWT-tid-only, no X-Tenant-Id header, no tenant subdomain) DOES land here and
            // shares this single bucket — the route's primary caller (the Web app) sends the header,
            // so this is an edge case, but a real one. Cap the shared bucket at the per-tenant limit
            // (GlobalFallbackPermitLimit == LlmPermitLimit) so it stays bounded without punitively
            // starving that caller class. TRUE per-tenant partitioning for header-less authenticated
            // callers requires resolving the authenticated tenant, which needs the deferred
            // UseRateLimiter() pipeline reorder (tracked as the E3 follow-up in the P2b plan).
            var permitLimit = tenantId == "__global__" ? GlobalFallbackPermitLimit : LlmPermitLimit;

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
    /// Resolves the rate-limit partition key for the <c>llm</c> policy. Resolution order:
    /// <list type="number">
    ///   <item><c>Items["TenantId"]</c> as string — honored first; <c>UseRateLimiter()</c> now runs
    ///   after <see cref="TenantResolutionMiddleware"/>, so this is populated for every
    ///   header/subdomain-identified tenant.</item>
    ///   <item>the <c>X-Tenant-Id</c> request header — fallback / belt-and-suspenders (same value
    ///   TenantResolutionMiddleware reads).</item>
    ///   <item><c>"__global__"</c> — the bounded shared fallback. Note this is NOT only pre-auth /
    ///   no-tenant traffic: a header-less authenticated caller (JWT-<c>tid</c>-only, no
    ///   <c>X-Tenant-Id</c> header, no tenant subdomain) also lands here and shares this bucket
    ///   (capped at <see cref="GlobalFallbackPermitLimit"/>) — the limiter stays before auth, so
    ///   its <c>tid</c> claim is not yet applied to <c>Items["TenantId"]</c>.</item>
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
