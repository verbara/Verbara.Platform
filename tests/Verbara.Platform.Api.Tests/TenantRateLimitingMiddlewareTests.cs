using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pins the request rate-limiting stage: the policy an endpoint names in its metadata is applied per partition,
/// a rejected request gets the shared <c>429</c> (with <c>Retry-After</c> and the rate-limit problem body) and never
/// reaches the rest of the pipeline, an endpoint that names no policy or opts out passes through, and the limiter is
/// released when the host stops.
/// </summary>
public sealed class TenantRateLimitingMiddlewareTests
{
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddSingleton(new TenantTierCache()).BuildServiceProvider();

    [Fact]
    public async Task InvokeAsync_ShouldReject429WithRetryAfterAndProblem_WhenTenantExceedsTheLlmLimit()
    {
        using var limiter = new TenantRequestRateLimiter(lifetime: null);
        var calls = 0;
        var middleware = new TenantRateLimitingMiddleware(_ => { calls++; return Task.CompletedTask; }, limiter);

        for (var i = 0; i < TenantRateLimitPolicy.LlmPermitLimit; i++)
        {
            var accepted = ContextFor("tenant-a", new EnableRateLimitingAttribute(TenantRateLimitPolicy.LlmPolicy));
            await middleware.InvokeAsync(accepted);
            accepted.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        var rejected = ContextFor("tenant-a", new EnableRateLimitingAttribute(TenantRateLimitPolicy.LlmPolicy));
        await middleware.InvokeAsync(rejected);

        calls.Should().Be(TenantRateLimitPolicy.LlmPermitLimit, "a rejected request must not reach the rest of the pipeline");
        rejected.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        int.Parse(rejected.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0);
        var body = BodyOf(rejected);
        body.Should().Contain("\"type\":\"rate_limit_exceeded\"").And.Contain(TenantRateLimitPolicy.RejectionDetail);
    }

    [Fact]
    public async Task InvokeAsync_ShouldKeepAnotherTenantsBudget_WhenOneTenantIsOverTheLimit()
    {
        using var limiter = new TenantRequestRateLimiter(lifetime: null);
        var middleware = new TenantRateLimitingMiddleware(_ => Task.CompletedTask, limiter);

        for (var i = 0; i <= TenantRateLimitPolicy.LlmPermitLimit; i++)
            await middleware.InvokeAsync(ContextFor("tenant-a", new EnableRateLimitingAttribute(TenantRateLimitPolicy.LlmPolicy)));

        var other = ContextFor("tenant-b", new EnableRateLimitingAttribute(TenantRateLimitPolicy.LlmPolicy));
        await middleware.InvokeAsync(other);

        other.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPassThrough_WhenTheEndpointDisablesRateLimiting()
    {
        using var limiter = new TenantRequestRateLimiter(lifetime: null);
        var calls = 0;
        var middleware = new TenantRateLimitingMiddleware(_ => { calls++; return Task.CompletedTask; }, limiter);

        for (var i = 0; i < TenantRateLimitPolicy.LlmPermitLimit + 5; i++)
        {
            await middleware.InvokeAsync(ContextFor("tenant-a",
                new EnableRateLimitingAttribute(TenantRateLimitPolicy.LlmPolicy), new DisableRateLimitingAttribute()));
        }

        calls.Should().Be(TenantRateLimitPolicy.LlmPermitLimit + 5);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPassThrough_WhenTheEndpointNamesNoPolicy()
    {
        using var limiter = new TenantRequestRateLimiter(lifetime: null);
        var calls = 0;
        var middleware = new TenantRateLimitingMiddleware(_ => { calls++; return Task.CompletedTask; }, limiter);

        for (var i = 0; i < TenantRateLimitPolicy.LlmPermitLimit + 5; i++)
            await middleware.InvokeAsync(ContextFor("tenant-a"));

        calls.Should().Be(TenantRateLimitPolicy.LlmPermitLimit + 5);
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenTheEndpointNamesAnUnknownPolicy()
    {
        using var limiter = new TenantRequestRateLimiter(lifetime: null);
        var middleware = new TenantRateLimitingMiddleware(_ => Task.CompletedTask, limiter);

        var act = () => middleware.InvokeAsync(ContextFor("tenant-a", new EnableRateLimitingAttribute("no-such-policy")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no-such-policy*");
    }

    [Fact]
    public async Task Constructor_ShouldReleaseTheLimiter_WhenTheApplicationStops()
    {
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        using var limiter = new TenantRequestRateLimiter(lifetime);

        lifetime.NotifyStopped();

        var act = async () => await limiter.AcquireAsync(
            ContextFor("tenant-a", new EnableRateLimitingAttribute(TenantRateLimitPolicy.LlmPolicy)));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static DefaultHttpContext ContextFor(string tenantId, params object[] endpointMetadata)
    {
        var context = new DefaultHttpContext { RequestServices = Services };
        context.Response.Body = new MemoryStream();
        context.Items["TenantId"] = new TenantId(tenantId);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(endpointMetadata), "test"));
        return context;
    }

    private static string BodyOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
