using System.Net;
using System.Net.Http.Json;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The anonymous second-factor route (<c>POST /api/v1/auth/mfa/verify</c>) carries its own limit per
/// resolved tenant and client address, plus a per-client limit across every tenant
/// (openspec <c>harden-mfa-verify-endpoint</c>, spec requirement "The anonymous second-factor endpoint
/// SHALL carry its own rate limit").
/// </summary>
/// <remarks>
/// The host runs at the documented defaults — 300 per tenant + client and 600 per client across tenants
/// per minute — so the numbers tested are the numbers shipped. Each test uses its own client address, so
/// no test starts with a budget another one spent. Requests that only need to spend budget carry a token
/// that names no challenge: a cheap <c>400</c> that touches no user.
/// </remarks>
public sealed class MfaVerifyRateLimitTests : IClassFixture<MfaVerifyRateLimitTests.Factory>
{
    private const int DefaultPerTenantClientLimit = 300;
    private const int DefaultAllTenantsClientLimit = 600;
    private const string Route = "/api/v1/auth/mfa/verify";
    private const string Tenant = AuthenticatedPlatformApiFactory.TestTenantId;
    private const string MfaSecret = "JBSWY3DPEHPK3PXP";
    private readonly Factory _factory;

    public MfaVerifyRateLimitTests(Factory factory) => _factory = factory;

    /// <summary>
    /// Every request reaches the app from a trusted proxy at 10.0.0.1, so the client address is the
    /// X-Forwarded-For value, exactly as behind an ingress. No limit is overridden.
    /// </summary>
    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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

    private static HttpRequestMessage Verify(string clientIp, string? tenant, string mfaToken = "no-such-challenge", string? code = "000000")
    {
        var json = $$"""{"mfaToken":"{{mfaToken}}","code":{{(code is null ? "null" : $"\"{code}\"")}}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Forwarded-For", clientIp);
        if (tenant is not null)
            request.Headers.Add("X-Tenant-Id", tenant);
        return request;
    }

    private static async Task SpendAsync(HttpClient client, int count, string clientIp, Func<int, string?> tenantFor)
    {
        for (var i = 0; i < count; i++)
        {
            using var within = await client.SendAsync(Verify(clientIp, tenantFor(i)));
            within.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "request {0} is within the limit", i + 1);
        }
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn429_WhenPerTenantClientLimitExceeded()
    {
        var client = _factory.CreateClient();
        const string clientIp = "198.51.100.110";

        await SpendAsync(client, DefaultPerTenantClientLimit, clientIp, _ => Tenant);

        using var rejected = await client.SendAsync(Verify(clientIp, Tenant));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "request {0} from one client for one tenant exceeds the default per-tenant-and-client limit",
            DefaultPerTenantClientLimit + 1);
        rejected.Headers.RetryAfter.Should().NotBeNull();
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("rate_limit_exceeded");
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn429_WhenClientRotatesTenantHeader()
    {
        var client = _factory.CreateClient();
        const string clientIp = "198.51.100.111";

        // A new tenant each time: each has a fresh tenant+client bucket, so only the per-client limit
        // across every tenant can stop the rotation.
        await SpendAsync(client, DefaultAllTenantsClientLimit, clientIp, i => $"rotated-tenant-{i}");

        using var rejected = await client.SendAsync(Verify(clientIp, "rotated-tenant-next"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "naming a new tenant on every request must not escape the per-client limit");
        rejected.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task MfaVerify_ShouldNotThrottleOtherTenant_WhenOneTenantExhaustsLimit()
    {
        var client = _factory.CreateClient();
        const string clientIp = "198.51.100.112";

        await SpendAsync(client, DefaultPerTenantClientLimit, clientIp, _ => Tenant);
        using var throttled = await client.SendAsync(Verify(clientIp, Tenant));
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the first tenant has spent its budget for this client");

        using var other = await client.SendAsync(Verify(clientIp, "tenant-other-002"));
        other.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "another tenant behind the same address has its own budget");
    }

    [Fact]
    public async Task MfaVerify_ShouldNotSpendChallengeAttempt_WhenRequestIsRateLimited()
    {
        var client = _factory.CreateClient();
        const string throttledIp = "198.51.100.113";
        const string freshIp = "198.51.100.114";
        var token = await SeedChallengeAsync();
        var wrong = WrongTotp();

        await SpendAsync(client, DefaultPerTenantClientLimit, throttledIp, _ => Tenant);
        using (var rejected = await client.SendAsync(Verify(throttledIp, Tenant, token, wrong)))
        {
            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
                "the throttled client's factor never reaches the handler");
        }

        // Had the rejected request spent an attempt, the second of these would exhaust the challenge.
        using var first = await client.SendAsync(Verify(freshIp, Tenant, token, wrong));
        using var second = await client.SendAsync(Verify(freshIp, Tenant, token, wrong));
        first.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await second.Content.ReadAsStringAsync()).Should().NotContain("mfa-challenge-exhausted",
            "a rate-limited request must not spend a challenge attempt");
    }

    /// <summary>Guard (green before the fix): the honest unhappy path never meets the limit.</summary>
    [Fact]
    public async Task MfaVerify_ShouldNotThrottle_WhenTwoWrongCodesPrecedeCorrectOne()
    {
        var client = _factory.CreateClient();
        const string clientIp = "198.51.100.115";
        var token = await SeedChallengeAsync();

        using var first = await client.SendAsync(Verify(clientIp, Tenant, token, WrongTotp()));
        using var second = await client.SendAsync(Verify(clientIp, Tenant, token, WrongTotp()));
        using var third = await client.SendAsync(Verify(clientIp, Tenant, token, CorrectTotp()));

        first.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        second.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        third.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>Seeds an MFA-enrolled Active user in the test tenant and a live challenge for it.</summary>
    private async Task<string> SeedChallengeAsync()
    {
        var userId = $"mfa-rl-{Guid.NewGuid():N}";
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(Tenant),
            Email = $"{userId}@seeded.test",
            DisplayName = userId,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = true,
            MfaSecret = MfaSecret,
            MfaConfirmedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var token = $"rl-challenge-{Guid.NewGuid():N}";
        await _factory.Services.GetRequiredService<IMfaPendingCache>().StoreAsync(token, new MfaPendingEntry
        {
            UserId = userId,
            TenantId = Tenant,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }, CancellationToken.None);
        return token;
    }

    private static string CorrectTotp() => new Totp(Base32Encoding.ToBytes(MfaSecret)).ComputeTotp(DateTime.UtcNow);

    private static string WrongTotp()
    {
        var totp = new Totp(Base32Encoding.ToBytes(MfaSecret));
        var now = DateTime.UtcNow;
        var window = Enumerable.Range(-2, 5).Select(k => totp.ComputeTotp(now.AddSeconds(30.0 * k))).ToHashSet();
        var candidate = int.Parse(totp.ComputeTotp(now), System.Globalization.CultureInfo.InvariantCulture);
        string code;
        do
        {
            candidate = (candidate + 7919) % 1_000_000;
            code = candidate.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
        while (window.Contains(code));
        return code;
    }
}
