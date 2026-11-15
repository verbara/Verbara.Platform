using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Request rate-limiting stage. For an endpoint whose metadata names a policy (<c>RequireRateLimiting</c> or
/// <see cref="EnableRateLimitingAttribute"/>), and is not marked <see cref="DisableRateLimitingAttribute"/>, it
/// takes a permit from <see cref="TenantRequestRateLimiter"/> before the rest of the pipeline runs, holds it for the
/// request, and answers a rejected request with <see cref="TenantRateLimitPolicy.WriteTooManyRequestsAsync"/>
/// (<c>429</c>, <c>Retry-After</c>, the rate-limit problem body). An endpoint that names no policy passes through.
/// It runs after <see cref="TenantResolutionMiddleware"/>, whose <c>Items["TenantId"]</c> the policies partition on,
/// and before authentication, so a request over its budget costs no credential check.
/// </summary>
internal sealed class TenantRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TenantRequestRateLimiter _limiter;

    public TenantRateLimitingMiddleware(RequestDelegate next, TenantRequestRateLimiter limiter)
    {
        _next = next;
        _limiter = limiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (GetPolicyName(context) is null)
        {
            await _next(context);
            return;
        }

        RateLimitLease lease;
        try
        {
            lease = await _limiter.AcquireAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away while waiting for a permit; there is no one left to answer.
            return;
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                await TenantRateLimitPolicy.WriteTooManyRequestsAsync(
                    context, lease, TenantRateLimitPolicy.RejectionDetail, context.RequestAborted);
                return;
            }

            await _next(context);
        }
    }

    /// <summary>
    /// The policy the current endpoint names, or <c>null</c> when it names none or opts out of rate limiting.
    /// Only named policies are supported: an endpoint that attaches a policy instance has no name to look up.
    /// </summary>
    internal static string? GetPolicyName(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata is null || metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
            return null;

        var enable = metadata.GetMetadata<EnableRateLimitingAttribute>();
        if (enable is null)
            return null;

        return enable.PolicyName
            ?? throw new InvalidOperationException(
                "This endpoint attaches a rate-limiting policy without a name; only named policies are supported.");
    }
}
