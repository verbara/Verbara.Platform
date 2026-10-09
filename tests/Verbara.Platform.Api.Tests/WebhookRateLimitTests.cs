using System.Net;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The anonymous provider-webhook route (<c>/api/v1/webhooks/{tenant}/{channel}</c>) is limited per
/// route tenant and client address before the channel configuration is looked up; requests for a
/// tenant with no active configuration also spend one per-client "unknown tenant" budget; and a
/// delivery body above 1 MB is refused with 413.
/// </summary>
/// <remarks>
/// Each test uses its own client address, so no test starts with a budget another one spent. The
/// channel is SMS because what the handler does with the body is irrelevant here: the limits and
/// the cap act before (or regardless of) the handler.
/// </remarks>
public sealed class WebhookRateLimitTests : IClassFixture<WebhookRateLimitTests.Factory>
{
    private const int TenantLimit = 3;
    private const int UnknownTenantLimit = 2;
    private const string ConfiguredTenant = AuthenticatedPlatformApiFactory.TestTenantId;
    private readonly Factory _factory;

    public WebhookRateLimitTests(Factory factory) => _factory = factory;

    /// <summary>
    /// Every request reaches the app from a trusted proxy at 10.0.0.1, so the client address is the
    /// X-Forwarded-For value, exactly as behind an ingress. The test tenant has an active SMS channel;
    /// the channel-config store is a substitute so a test can count the lookups.
    /// </summary>
    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        public ITenantChannelConfigStore ConfigStore { get; } = CreateConfigStore();

        private static ITenantChannelConfigStore CreateConfigStore()
        {
            var store = Substitute.For<ITenantChannelConfigStore>();
            var config = new TenantChannelConfig
            {
                TenantId = new TenantId(ConfiguredTenant),
                Channel = ChannelType.Sms,
                Credentials = new Dictionary<string, string>(),
                IsActive = true,
            };
            store.GetAsync(Arg.Is<TenantId>(t => t.Value == ConfiguredTenant), ChannelType.Sms, Arg.Any<CancellationToken>())
                .Returns(config);
            return store;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Webhooks:RateLimit:PerMinutePerClient", TenantLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Webhooks:RateLimit:UnknownTenantPerMinutePerClient", UnknownTenantLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("ForwardedHeaders:TrustedProxies:0", "10.0.0.0/8");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, ProxyPeerStartupFilter>();
                services.AddSingleton(ConfigStore);
            });
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

    private static HttpRequestMessage Post(string tenant, string clientIp, int bodyBytes = 2)
    {
        var body = bodyBytes <= 2 ? Encoding.UTF8.GetBytes("{}") : new byte[bodyBytes];
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/webhooks/{tenant}/sms")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Forwarded-For", clientIp);
        return request;
    }

    [Fact]
    public async Task PostWebhook_ShouldReturn429_WhenClientExceedsTenantBudget()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < TenantLimit; i++)
        {
            using var within = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.10"));
            within.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "request {0} is within the limit", i + 1);
        }

        using var rejected = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.10"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("rate_limit_exceeded");
    }

    [Fact]
    public async Task PostWebhook_ShouldServeSecondClient_WhenFirstIsThrottled()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i <= TenantLimit; i++)
            (await client.SendAsync(Post(ConfiguredTenant, "198.51.100.20"))).Dispose();

        using var throttled = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.20"));
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var other = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.21"));
        other.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "another client has its own budget");
    }

    [Fact]
    public async Task GetWebhook_ShouldReturn429_WhenClientExceedsTenantBudget()
    {
        var client = _factory.CreateClient();

        HttpRequestMessage Verify()
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/webhooks/{ConfiguredTenant}/whatsapp?hub.mode=subscribe&hub.verify_token=t&hub.challenge=c");
            request.Headers.Add("X-Forwarded-For", "198.51.100.30");
            return request;
        }

        for (var i = 0; i < TenantLimit; i++)
        {
            using var within = await client.SendAsync(Verify());
            within.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        using var rejected = await client.SendAsync(Verify());
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task PostWebhook_ShouldNotCountAgainstTenant_WhenTenantUnknown()
    {
        var client = _factory.CreateClient();
        const string clientIp = "198.51.100.40";

        // A new unknown tenant each time: each has a fresh tenant+client bucket, so only the shared
        // unknown-tenant bucket can stop the enumeration.
        for (var i = 0; i < UnknownTenantLimit; i++)
        {
            using var unknown = await client.SendAsync(Post($"ghost-{i}", clientIp));
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var enumerated = await client.SendAsync(Post("ghost-next", clientIp));
        enumerated.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "naming a new unknown tenant each time must not escape the limit");

        // The configured tenant's budget for the same client is untouched: all of it is still there.
        for (var i = 0; i < TenantLimit; i++)
        {
            using var real = await client.SendAsync(Post(ConfiguredTenant, clientIp));
            real.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "request {0} spends the real tenant's own budget", i + 1);
        }
    }

    [Fact]
    public async Task PostWebhook_ShouldNotLookUpChannelConfig_WhenRequestIsRejected()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < TenantLimit; i++)
            (await client.SendAsync(Post(ConfiguredTenant, "198.51.100.70"))).Dispose();

        var lookupsBefore = ConfigLookups();
        for (var i = 0; i < 5; i++)
        {
            using var rejected = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.70"));
            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        ConfigLookups().Should().Be(lookupsBefore, "the limit applies before the channel configuration is read");
    }

    private int ConfigLookups() =>
        _factory.ConfigStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITenantChannelConfigStore.GetAsync));

    [Fact]
    public async Task PostWebhook_ShouldReturn413_WhenBodyExceedsCap()
    {
        var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.50", bodyBytes: 2 * 1024 * 1024));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task PostWebhook_ShouldReadBody_WhenBelowCap()
    {
        var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post(ConfiguredTenant, "198.51.100.60", bodyBytes: 900 * 1024));

        response.StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge);
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }
}
