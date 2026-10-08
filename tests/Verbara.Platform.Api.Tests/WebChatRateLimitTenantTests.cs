using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The WebChat limits are also partitioned per tenant, so behind a proxy the app does not trust
/// (every visitor arrives from the proxy's address) one tenant's traffic cannot use up another
/// tenant's budget, and requests for an unknown tenant or session cannot use up a real one's.
/// </summary>
/// <remarks>Each test gets its own host, so no test starts with a budget another one spent.</remarks>
public sealed class WebChatRateLimitTenantTests : IDisposable
{
    private const int Limit = 3;
    private const string OtherTenantId = "tenant-test-002";
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Every request reaches the app from 10.0.0.1 and no proxy is trusted, exactly as behind a
    /// gateway whose address is not in <c>ForwardedHeaders:TrustedProxies</c>: the app sees one
    /// client address for every visitor.
    /// </summary>
    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebChatRateLimitPolicy.SessionsLimitKey, Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting(WebChatRateLimitPolicy.MessagesLimitKey, Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ProxyPeerStartupFilter>());
        }

        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            SeedTestCustomerTenant(host.Services, OtherTenantId);
            return host;
        }

        private sealed class ProxyPeerStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }

    private static HttpRequestMessage CreateSessionRequest(string tenantId) =>
        new(HttpMethod.Post, "/api/v1/webchat/sessions")
        {
            Content = JsonContent.Create(new { tenantId }),
        };

    private static HttpRequestMessage SendMessageRequest(string sessionId) =>
        new(HttpMethod.Post, $"/api/v1/webchat/sessions/{sessionId}/messages")
        {
            Content = JsonContent.Create(new { text = "hi" }),
        };

    private static async Task<string> CreateSessionAsync(HttpClient client, string tenantId)
    {
        using var response = await client.SendAsync(CreateSessionRequest(tenantId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SessionBody>();
        return body!.SessionId;
    }

    private sealed record SessionBody(string SessionId, string WsUrl);

    [Fact]
    public async Task CreateSession_ShouldKeepOtherTenantsBudget_WhenOneTenantExhaustsItsOwnBehindTheSameProxy()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < Limit; i++)
        {
            using var ok = await client.SendAsync(CreateSessionRequest(AuthenticatedPlatformApiFactory.TestTenantId));
            ok.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is within the limit", i + 1);
        }

        using var rejected = await client.SendAsync(CreateSessionRequest(AuthenticatedPlatformApiFactory.TestTenantId));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();

        using var otherTenant = await client.SendAsync(CreateSessionRequest(OtherTenantId));
        otherTenant.StatusCode.Should().Be(HttpStatusCode.OK, "another tenant has its own budget behind the same proxy");
    }

    [Fact]
    public async Task CreateSession_ShouldKeepARealTenantsBudget_WhenRequestsForUnknownTenantsAreRejected()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < Limit; i++)
        {
            using var unknown = await client.SendAsync(CreateSessionRequest($"no-such-tenant-{i}"));
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var rejected = await client.SendAsync(CreateSessionRequest("no-such-tenant-x"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "unknown tenants share one budget, so naming a new one each time does not escape the limit");

        using var real = await client.SendAsync(CreateSessionRequest(OtherTenantId));
        real.StatusCode.Should().Be(HttpStatusCode.OK, "requests for unknown tenants must not spend a real tenant's budget");
    }

    [Fact]
    public async Task SendMessage_ShouldKeepOtherTenantsBudget_WhenOneTenantExhaustsItsOwnBehindTheSameProxy()
    {
        var client = _factory.CreateClient();
        var noisy = await CreateSessionAsync(client, AuthenticatedPlatformApiFactory.TestTenantId);
        var quiet = await CreateSessionAsync(client, OtherTenantId);

        for (var i = 0; i < Limit; i++)
        {
            using var ok = await client.SendAsync(SendMessageRequest(noisy));
            ok.StatusCode.Should().Be(HttpStatusCode.OK, "message {0} is within the limit", i + 1);
        }

        using var rejected = await client.SendAsync(SendMessageRequest(noisy));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var otherTenant = await client.SendAsync(SendMessageRequest(quiet));
        otherTenant.StatusCode.Should().Be(HttpStatusCode.OK, "another tenant's session has its own budget behind the same proxy");
    }

    [Fact]
    public async Task SendMessage_ShouldKeepARealSessionsBudget_WhenRequestsForUnknownSessionsAreRejected()
    {
        var client = _factory.CreateClient();
        var session = await CreateSessionAsync(client, OtherTenantId);

        for (var i = 0; i <= Limit; i++)
            (await client.SendAsync(SendMessageRequest($"no-such-session-{i}"))).Dispose();

        using var real = await client.SendAsync(SendMessageRequest(session));
        real.StatusCode.Should().Be(HttpStatusCode.OK, "requests for unknown sessions must not spend a real session's budget");
    }
}
