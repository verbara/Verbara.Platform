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
    /// E3 — per-tenant permit limit for the <c>llm</c> policy (sliding 1-minute window), the only policy of this
    /// limiter (<c>RequireRateLimiting</c>) attached to an endpoint (the AI typification suggestion); the anonymous
    /// WebChat and webhook routes have their own endpoint-filter limiter (<see cref="AnonymousRateLimitPolicy"/>).
    /// LLM calls cost money at every tier, so this policy ignores the tenant's rate-limit tier. Each API key, and
    /// otherwise each resolved tenant, gets its own bucket (see <see cref="ResolveLlmTarget"/>).
    /// </summary>
    internal const int LlmPermitLimit = 30;

    /// <summary>
    /// E3 — permit limit for the shared <c>t?:</c> partition of the <c>llm</c> policy: requests whose tenant is
    /// unknown when the limiter runs and that carry no API key. That includes a header-less, subdomain-less JWT
    /// caller identified only by its <c>tid</c>: the limiter runs before <c>UseAuthentication()</c> (throttle
    /// before auth work), and authentication is what writes that caller's <c>Items["TenantId"]</c>.
    /// Equal to <see cref="LlmPermitLimit"/> so the bucket stays bounded without starving that caller class.
    /// </summary>
    internal const int GlobalFallbackPermitLimit = 30;

    /// <summary>Window length for the <c>llm</c> sliding-window limiter.</summary>
    internal static readonly TimeSpan LlmWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the partition key and tier of the <c>per-tenant</c> policy. A resolved tenant
    /// (<see cref="RateLimitPartitionKeys.TryGetResolvedTenant"/>) gets <c>t:{tenantId}</c> at its tier from
    /// <see cref="Services.TenantTierCache"/> (or <see cref="RateLimitTier.Standard"/>); an unknown tenant gets the
    /// shared <c>t?:</c> partition at <see cref="RateLimitTier.Standard"/> — limited at least as strictly as a
    /// known tenant, never <see cref="RateLimitTier.Unlimited"/>.
    /// </summary>
    internal static (string PartitionKey, RateLimitTier Tier) ResolvePerTenantTarget(HttpContext context)
    {
        if (!RateLimitPartitionKeys.TryGetResolvedTenant(context, out var tenantId))
            return (RateLimitPartitionKeys.UnknownTenant, RateLimitTier.Standard);

        var tier = context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId.Value)
            ?? RateLimitTier.Standard;
        return (RateLimitPartitionKeys.ForTenant(tenantId), tier);
    }

    /// <summary>
    /// Resolves the partition key and permit limit of the <c>llm</c> policy. A request carrying an API key is
    /// keyed on the key (<see cref="RateLimitPartitionKeys.TryGetApiKeyPartition"/>) at <see cref="LlmPermitLimit"/>:
    /// the API-key handler replaces the tenant afterwards, so keying it on the tenant the request named would let
    /// one key buy a fresh bucket per <c>X-Tenant-Id</c> or Host. Otherwise the tenant the pipeline resolved
    /// (<c>Items["TenantId"]</c>, from the subdomain or the <c>X-Tenant-Id</c> header — the header is not read
    /// again here): resolved → <c>t:{tenantId}</c> at <see cref="LlmPermitLimit"/>; unknown → the shared
    /// <c>t?:</c> partition at <see cref="GlobalFallbackPermitLimit"/>. That tenant is not yet verified (the
    /// limiter runs before authentication), so an anonymous caller can still name another tenant's bucket.
    /// </summary>
    internal static (string PartitionKey, int PermitLimit) ResolveLlmTarget(HttpContext context)
    {
        if (RateLimitPartitionKeys.TryGetApiKeyPartition(context, out var keyPartition))
            return (keyPartition, LlmPermitLimit);

        return RateLimitPartitionKeys.TryGetResolvedTenant(context, out var tenantId)
            ? (RateLimitPartitionKeys.ForTenant(tenantId), LlmPermitLimit)
            : (RateLimitPartitionKeys.UnknownTenant, GlobalFallbackPermitLimit);
    }

    public static void ConfigureRateLimiting(RateLimiterOptions options)
    {
        options.RejectionStatusCode = 429;

        // Registered but attached to no endpoint, and no global limiter is configured: this policy limits nothing.
        // It is kept until the change wire-api-rate-limiting-enforcement replaces it with a per-IP backstop.
        options.AddSlidingWindowLimiter("global-safety", o =>
        {
            o.Window = TimeSpan.FromMinutes(1);
            o.SegmentsPerWindow = 6;
            o.PermitLimit = 3000;
        });

        // Per-tenant tier policy: registered but attached to no endpoint, so it limits nothing. Its partition
        // resolution (ResolvePerTenantTarget, unit-tested) is correct so that whoever attaches it gets a policy that
        // fails closed. wire-api-rate-limiting-enforcement replaces it with a verified-tenant stage.
        options.AddPolicy("per-tenant", context =>
        {
            var (partitionKey, tier) = ResolvePerTenantTarget(context);

            if (tier.IsUnlimited())
                return RateLimitPartition.GetNoLimiter(partitionKey);

            return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                PermitLimit = tier.GetPermitLimit(),
                AutoReplenishment = true,
            });
        });

        // E3 — the per-tenant LLM policy, attached to the AI-suggestion route only; it is the only policy of this
        // limiter applied to any endpoint (the anonymous WebChat and webhook routes use the endpoint-filter
        // AnonymousRateLimitPolicy). It ignores the tier (an Unlimited-tier tenant is throttled too).
        // ResolveLlmTarget keys it on the API key when one is sent, else on the tenant TenantResolutionMiddleware
        // resolved before UseRateLimiter(); a request with neither shares the bounded t?: bucket.
        options.AddPolicy("llm", context =>
        {
            var (partitionKey, permitLimit) = ResolveLlmTarget(context);

            return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
            {
                Window = LlmWindow,
                SegmentsPerWindow = 6,
                PermitLimit = permitLimit,
                AutoReplenishment = true,
            });
        });

        // Custom 429 response
        options.OnRejected = (context, ct) =>
            new ValueTask(WriteTooManyRequestsAsync(context.HttpContext, context.Lease, "Tenant rate limit exceeded", ct));
    }

    /// <summary>
    /// Writes the shared 429 response for a rejected <paramref name="lease"/>: a <c>rate_limit_exceeded</c>
    /// problem and a <c>Retry-After</c> header (the lease's, or 30 seconds). Used by the rate-limiting
    /// middleware and by the limits applied in endpoint filters (<see cref="WebChatRateLimitPolicy"/>).
    /// </summary>
    internal static async Task WriteTooManyRequestsAsync(
        HttpContext httpContext, RateLimitLease lease, string detail, CancellationToken ct)
    {
        httpContext.Response.StatusCode = 429;
        httpContext.Response.ContentType = "application/json";

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
            ? (int)retryAfterValue.TotalSeconds : 30;

        httpContext.Response.Headers["Retry-After"] = retryAfter.ToString(CultureInfo.InvariantCulture);

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = "rate_limit_exceeded",
            Title = "Too Many Requests",
            Status = 429,
            Detail = detail,
        };
        problem.Extensions["retryAfter"] = retryAfter;
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(problem, ApiJsonContext.Default.ProblemDetails), ct);
    }
}
