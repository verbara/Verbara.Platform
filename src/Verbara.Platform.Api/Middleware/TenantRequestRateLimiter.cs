using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// The one request limiter behind <see cref="TenantRateLimitingMiddleware"/>: a partitioned limiter whose
/// partitions are the <see cref="TenantRateLimitPolicy"/> partitions of the policy each endpoint names, keyed by
/// policy and partition so two policies never share a bucket. It is a host singleton and is disposed when the
/// host stops and again (a no-op) when the service provider is disposed. Disposing it stops its replenishment
/// timer, so a stopped host leaves nothing in the process-wide timer queue that keeps it reachable (#586).
/// </summary>
internal sealed class TenantRequestRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;
    private int _disposed;

    public TenantRequestRateLimiter(IHostApplicationLifetime? lifetime)
    {
        _limiter = PartitionedRateLimiter.Create<HttpContext, PolicyPartitionKey>(static context =>
        {
            var policyName = TenantRateLimitingMiddleware.GetPolicyName(context)
                ?? throw new InvalidOperationException("The request limiter was asked for an endpoint that names no rate-limiting policy.");
            var partition = TenantRateLimitPolicy.GetPartition(policyName, context)
                ?? throw new InvalidOperationException(
                    $"This endpoint requires a rate limiting policy with name {policyName}, but no such policy exists.");
            return RateLimitPartition.Get(
                new PolicyPartitionKey(policyName, partition.PartitionKey),
                key => partition.Factory(key.PartitionKey));
        });
        lifetime?.ApplicationStopped.Register(Dispose);
    }

    public ValueTask<RateLimitLease> AcquireAsync(HttpContext context)
    {
        var lease = _limiter.AttemptAcquire(context);
        if (lease.IsAcquired)
            return ValueTask.FromResult(lease);

        // Same order as the framework stage: an immediate attempt first, then a wait that a policy with a queue
        // can satisfy (every policy here has none, so the wait answers at once).
        lease.Dispose();
        return _limiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _limiter.Dispose();
    }

    private readonly record struct PolicyPartitionKey(string PolicyName, string PartitionKey);
}

/// <summary>Registration of the tenant request rate-limiting stage.</summary>
internal static class TenantRequestRateLimitingExtensions
{
    /// <summary>
    /// Registers the <see cref="TenantRequestRateLimiter"/> singleton that
    /// <see cref="TenantRateLimitingMiddleware"/> uses; the host owns its lifetime.
    /// </summary>
    public static IServiceCollection AddTenantRequestRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton(static sp => new TenantRequestRateLimiter(sp.GetService<IHostApplicationLifetime>()));
        return services;
    }

    /// <summary>
    /// Adds the tenant request rate-limiting stage. It applies the <see cref="TenantRateLimitPolicy"/> policy an
    /// endpoint names through <c>RequireRateLimiting</c> or <see cref="EnableRateLimitingAttribute"/>, and skips
    /// an endpoint marked with <see cref="DisableRateLimitingAttribute"/>.
    /// </summary>
    public static IApplicationBuilder UseTenantRequestRateLimiting(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantRateLimitingMiddleware>();
}
