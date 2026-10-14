using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// First coverage for <see cref="RateLimitHeadersMiddleware"/>, invoked directly. The seam is faithful
/// to production: <c>TenantResolutionMiddleware</c> writes <c>Items["TenantId"]</c> as a boxed
/// <see cref="TenantId"/> struct, never a string (wire-api-rate-limiting T1-D1). The response feature models
/// a server: headers become read-only once the response starts, and <c>OnStarting</c> callbacks run first.
/// </summary>
public sealed class RateLimitHeadersMiddlewareTests
{
    private static DefaultHttpContext ContextFor(TenantId? tenantId, TenantTierCache tierCache)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tierCache);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Features.Set<IHttpResponseFeature>(new ServerResponseFeature());
        if (tenantId is not null)
            context.Items["TenantId"] = tenantId.Value;
        return context;
    }

    /// <summary>Invokes the middleware with a no-op <c>next</c>, then starts the response as the server would.</summary>
    private static async Task InvokeAndStartAsync(RateLimitHeadersMiddleware middleware, HttpContext context)
    {
        await middleware.InvokeAsync(context);
        await ((ServerResponseFeature)context.Features.Get<IHttpResponseFeature>()!).StartAsync();
    }

    private sealed class ServerResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];
        private readonly HeaderDictionary _headers = new();
        private bool _started;

        public ServerResponseFeature() => Headers = _headers;

        public override bool HasStarted => _started;

        public override void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public async Task StartAsync()
        {
            if (_started)
                return;
            for (var i = _onStarting.Count - 1; i >= 0; i--)
                await _onStarting[i].Callback(_onStarting[i].State);
            _started = true;
            _headers.IsReadOnly = true;
        }
    }

    [Fact]
    public async Task InvokeAsync_ShouldWriteHeadersBeforeResponseStarts_WhenNextStartsResponse()
    {
        // Every real endpoint starts the response (writes its body) inside next; headers written after that
        // throw. The middleware must register them to be written when the response starts.
        var cache = new TenantTierCache();
        cache.SetTier("tenant-a", RateLimitTier.Standard);
        var context = ContextFor(new TenantId("tenant-a"), cache);
        var server = (ServerResponseFeature)context.Features.Get<IHttpResponseFeature>()!;
        var middleware = new RateLimitHeadersMiddleware(_ => server.StartAsync());

        await middleware.InvokeAsync(context);

        server.HasStarted.Should().BeTrue();
        context.Response.Headers["X-RateLimit-Limit"].ToString().Should().Be("300");
        context.Response.Headers["X-RateLimit-Tenant"].ToString().Should().Be("tenant-a");
    }

    [Fact]
    public async Task InvokeAsync_ShouldWriteTierLimitHeaders_WhenTenantResolved()
    {
        var cache = new TenantTierCache();
        cache.SetTier("tenant-a", RateLimitTier.Standard);
        var context = ContextFor(new TenantId("tenant-a"), cache);
        var middleware = new RateLimitHeadersMiddleware(_ => Task.CompletedTask);

        await InvokeAndStartAsync(middleware, context);

        context.Response.Headers["X-RateLimit-Limit"].ToString().Should().Be("300");
        context.Response.Headers["X-RateLimit-Tenant"].ToString().Should().Be("tenant-a");
        context.Response.Headers.ContainsKey("X-RateLimit-Reset").Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldWriteNoHeaders_WhenTenantUnresolved()
    {
        var context = ContextFor(tenantId: null, new TenantTierCache());
        var middleware = new RateLimitHeadersMiddleware(_ => Task.CompletedTask);

        await InvokeAndStartAsync(middleware, context);

        context.Response.Headers.ContainsKey("X-RateLimit-Limit").Should().BeFalse();
        context.Response.Headers.ContainsKey("X-RateLimit-Reset").Should().BeFalse();
        context.Response.Headers.ContainsKey("X-RateLimit-Tenant").Should().BeFalse();
    }
}
