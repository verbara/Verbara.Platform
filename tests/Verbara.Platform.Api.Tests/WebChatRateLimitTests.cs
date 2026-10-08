using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The anonymous WebChat endpoints are limited per client address — the address the app trusts
/// after <c>UseForwardedHeaders</c> — and the limit leaves other clients and the authenticated
/// console endpoints untouched.
/// </summary>
public sealed class WebChatRateLimitTests : IClassFixture<WebChatRateLimitTests.Factory>
{
    private const int Limit = 3;
    private readonly Factory _factory;

    public WebChatRateLimitTests(Factory factory) => _factory = factory;

    /// <summary>
    /// Every request reaches the app from a trusted proxy at 10.0.0.1, so the client address is
    /// the X-Forwarded-For value, exactly as in a deployment behind an ingress.
    /// </summary>
    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebChatRateLimitPolicy.SessionsLimitKey, Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting(WebChatRateLimitPolicy.MessagesLimitKey, Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("ForwardedHeaders:TrustedProxies:0", "10.0.0.0/8");
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ProxyPeerStartupFilter>());
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

    private static HttpRequestMessage CreateSessionRequest(string clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webchat/sessions")
        {
            Content = JsonContent.Create(new { tenantId = AuthenticatedPlatformApiFactory.TestTenantId }),
        };
        request.Headers.Add("X-Forwarded-For", clientIp);
        return request;
    }

    [Fact]
    public async Task CreateSession_ShouldReturn429WithRetryAfter_WhenOneClientExceedsTheLimit()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < Limit; i++)
        {
            using var ok = await client.SendAsync(CreateSessionRequest("203.0.113.10"));
            ok.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is within the limit", i + 1);
        }

        using var rejected = await client.SendAsync(CreateSessionRequest("203.0.113.10"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();

        using var otherClient = await client.SendAsync(CreateSessionRequest("203.0.113.11"));
        otherClient.StatusCode.Should().Be(HttpStatusCode.OK, "another client has its own budget");
    }

    [Fact]
    public async Task SendMessage_ShouldReturn429_WhenOneClientExceedsTheMessageLimit()
    {
        var client = _factory.CreateClient();

        HttpRequestMessage Send()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webchat/sessions/no-such-session/messages")
            {
                Content = JsonContent.Create(new { text = "hi" }),
            };
            request.Headers.Add("X-Forwarded-For", "203.0.113.20");
            return request;
        }

        for (var i = 0; i < Limit; i++)
        {
            using var within = await client.SendAsync(Send());
            within.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var rejected = await client.SendAsync(Send());
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ConsoleEndpoints_ShouldNotBeLimited_WhenSameClientExhaustedTheWebChatLimit()
    {
        var anonymous = _factory.CreateClient();
        for (var i = 0; i <= Limit; i++)
            (await anonymous.SendAsync(CreateSessionRequest("203.0.113.30"))).Dispose();

        var console = _factory.CreateAuthenticatedClient();
        for (var i = 0; i < Limit * 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/conversations");
            request.Headers.Add("X-Forwarded-For", "203.0.113.30");
            using var response = await console.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void ResolveClientKey_ShouldGroupIpv6ByPrefix_WhenAddressGiven(string address, string expected)
    {
        WebChatRateLimitPolicy.ResolveClientKey(IPAddress.Parse(address)).Should().Be(expected);
    }

    [Fact]
    public void ResolveClientKey_ShouldUseSharedKey_WhenAddressUnknown()
    {
        WebChatRateLimitPolicy.ResolveClientKey(null).Should().Be("unknown");
    }
}
