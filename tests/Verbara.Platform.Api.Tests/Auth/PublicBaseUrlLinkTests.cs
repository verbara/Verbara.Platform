using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The password-reset link mailed by <c>POST /auth/forgot-password</c> and the OIDC
/// <c>redirect_uri</c> come from the configured public address of the console
/// (<c>Platform:PublicBaseUrl</c>, or the single <c>CORS_ORIGINS</c> entry), never from the request.
/// The Host header — and X-Forwarded-Host — is whatever the sender writes: a reset requested with a
/// forged Host used to mail the victim the platform's own email with a link, and its single-use
/// token, on the sender's domain. These tests run the real host pipeline.
/// </summary>
public sealed class PublicBaseUrlLinkTests
{
    private const string TenantId = "reset-link-tenant";
    private const string Email = "victim@reset-link.test";
    private const string UserId = "reset-link-victim";
    private const string PublicBaseUrl = "https://console.example.test";
    private const string ForgedHost = "attacker.example";
    // TenantResolutionMiddleware reads a tenant from the Host's first label, so a forged
    // "attacker.example" resolves tenant "attacker" (the OIDC group's plan gate checks it).
    private const string ForgedHostTenant = "attacker";
    private const string IdpAuthority = "https://idp.example.test";
    private const string OidcCallbackPath = "/api/auth/oidc/callback";
    // A public address per tenant: {tenant} is filled with the tenant's host label.
    private const string TenantPublicBaseUrl = "https://{tenant}.example.test";

    // ─── Password-reset link ─────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_ShouldBuildResetLinkFromPublicBaseUrl_WhenHostHeaderIsForged()
    {
        using var factory = new LinkFactory(new()
        {
            ["Platform:PublicBaseUrl"] = PublicBaseUrl,
            ["CORS_ORIGINS"] = "https://other.example.test",
        });
        using var request = ForgotPasswordRequest(TenantId);
        request.Headers.Host = ForgedHost;
        request.Headers.Add("X-Forwarded-Host", ForgedHost);
        request.Headers.Add("X-Forwarded-Proto", "http");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var link = factory.SingleResetLink();
        link.Should().StartWith($"{PublicBaseUrl}/reset-password?token=",
            because: "the link must point at the configured console, whatever Host the request carried");
        var mail = factory.Sent.Should().ContainSingle().Subject;
        mail.Recipients.Should().ContainSingle().Which.Email.Should().Be(Email);
        mail.TextBody.Should().Contain(link).And.NotContain(ForgedHost);
    }

    [Fact]
    public async Task ForgotPassword_ShouldBuildResetLinkFromTheSingleCorsOrigin_WhenPublicBaseUrlIsNotSet()
    {
        using var factory = new LinkFactory(new()
        {
            ["Platform:PublicBaseUrl"] = null,
            ["CORS_ORIGINS"] = "https://app.example.test/",
        });
        using var request = ForgotPasswordRequest(TenantId);
        request.Headers.Host = ForgedHost;

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.SingleResetLink().Should().StartWith("https://app.example.test/reset-password?token=",
            because: "a deployment that names exactly one origin in CORS_ORIGINS keeps sending reset links without new configuration");
    }

    [Fact]
    public async Task ForgotPassword_ShouldKeepTheConfiguredLink_WhenTheTenantComesFromAForgedSubdomain()
    {
        // No tenant in the body: the tenant comes from the Host's first label, which stays a
        // feature. The link must not follow the rest of that Host.
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = PublicBaseUrl });
        using var request = ForgotPasswordRequest(tenantId: "");
        request.Headers.Host = $"{TenantId}.{ForgedHost}";

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.SingleResetLink().Should().StartWith($"{PublicBaseUrl}/reset-password?token=");
    }

    [Fact]
    public async Task ForgotPassword_ShouldPercentEncodeTheTokenInTheResetLink_WhenTheLinkIsMailed()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = PublicBaseUrl });
        using var request = ForgotPasswordRequest(TenantId);

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = factory.StoredTokens.Should().ContainSingle().Subject;
        var link = factory.SingleResetLink();
        link.Should().Be($"{PublicBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}",
            because: "the token is standard Base64, whose '+', '/' and '=' are not literal in a query string");
        QueryHelpers.ParseQuery(new Uri(link).Query)["token"].ToString().Should().Be(token,
            because: "the console reads the token back the way a browser parses a query string, where a bare '+' is a space");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("https://a.example.test,https://b.example.test")]
    public async Task ForgotPassword_ShouldAnswer200WithoutMailingALink_WhenNoPublicBaseUrlCanBeResolved(string? corsOrigins)
    {
        using var factory = new LinkFactory(new()
        {
            ["Platform:PublicBaseUrl"] = null,
            ["CORS_ORIGINS"] = corsOrigins,
        });
        using var request = ForgotPasswordRequest(TenantId);
        request.Headers.Host = ForgedHost;

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "the answer never reveals whether the email exists");
        factory.Sent.Should().BeEmpty(because: "without a configured address there is no link that is safe to send");
        factory.TemplateVariables.Should().BeEmpty();
        factory.StoredTokens.Should().BeEmpty(because: "a token that cannot be delivered is not minted");
    }

    [Fact]
    public async Task ForgotPassword_ShouldAnswer200WithoutMailingALink_WhenPublicBaseUrlIsNotAnAbsoluteHttpUrl()
    {
        using var factory = new LinkFactory(new()
        {
            ["Platform:PublicBaseUrl"] = "console.example.test",
            ["CORS_ORIGINS"] = "https://app.example.test",
        });
        using var request = ForgotPasswordRequest(TenantId);
        request.Headers.Host = ForgedHost;

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Sent.Should().BeEmpty(
            because: "a set but unusable address is an operator error to surface, not a reason to fall back to another origin");
    }

    // ─── OIDC redirect_uri ───────────────────────────────────────────────────

    [Fact]
    public async Task OidcLogin_ShouldSendTheCallbackUnderPublicBaseUrlAsRedirectUri_WhenHostHeaderIsForged()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = PublicBaseUrl });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/oidc/login?tenant_id={TenantId}&return_url=%2F");
        request.Headers.Host = ForgedHost;

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.GetLeftPart(UriPartial.Path).Should().Be($"{IdpAuthority}/authorize");
        QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString()
            .Should().Be($"{PublicBaseUrl}{OidcCallbackPath}");
    }

    [Fact]
    public async Task OidcLogin_ShouldSendTheCallbackAtTheOriginAsRedirectUri_WhenPublicBaseUrlHasAPath()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = "https://example.test/console" });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/api/v1/auth/oidc/login?tenant_id={TenantId}&return_url=%2F");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"].ToString()
            .Should().Be($"https://example.test{OidcCallbackPath}",
                because: "the console's path is not the API's: the callback is answered at the host's root");
    }

    [Fact]
    public async Task ForgotPassword_ShouldKeepTheConsolePathInTheResetLink_WhenPublicBaseUrlHasAPath()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = "https://example.test/console" });
        using var request = ForgotPasswordRequest(TenantId);

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.SingleResetLink().Should().StartWith("https://example.test/console/reset-password?token=",
            because: "the reset page is a console page, under the console's path");
    }

    [Fact]
    public async Task OidcCallback_ShouldExchangeTheCodeWithTheCallbackUnderPublicBaseUrl_WhenHostHeaderIsForged()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = PublicBaseUrl });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{OidcCallbackPath}?code=authorization-code");
        request.Headers.Host = ForgedHost;
        request.Headers.Add("Cookie", $"oidc_state={factory.ProtectFlowState()}");

        var response = await client.SendAsync(request);

        // The stubbed exchange throws once it has recorded its arguments, so the handler answers 400.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.ExchangeRedirectUris.Should().ContainSingle()
            .Which.Should().Be($"{PublicBaseUrl}{OidcCallbackPath}",
                because: "the token request must repeat the redirect_uri the authorization request sent");
    }

    [Fact]
    public async Task OidcLogin_ShouldRefuseWithoutRedirectingToTheProvider_WhenNoPublicBaseUrlCanBeResolved()
    {
        using var factory = new LinkFactory(new()
        {
            ["Platform:PublicBaseUrl"] = null,
            ["CORS_ORIGINS"] = "https://a.example.test,https://b.example.test",
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/oidc/login?tenant_id={TenantId}&return_url=%2F");
        request.Headers.Host = ForgedHost;

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.Location.Should().BeNull();
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeFalse(
            "no sign-in flow starts, so no state cookie is issued ({0})", string.Join("; ", cookies ?? []));
        (await response.Content.ReadAsStringAsync()).Should().Contain("Platform:PublicBaseUrl");
    }

    [Fact]
    public async Task OidcCallback_ShouldRefuseWithoutExchangingTheCode_WhenNoPublicBaseUrlCanBeResolved()
    {
        using var factory = new LinkFactory(new()
        {
            ["Platform:PublicBaseUrl"] = null,
            ["CORS_ORIGINS"] = "*",
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{OidcCallbackPath}?code=authorization-code");
        request.Headers.Host = ForgedHost;
        request.Headers.Add("Cookie", $"oidc_state={factory.ProtectFlowState()}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        factory.ExchangeRedirectUris.Should().BeEmpty();
    }

    // ─── A public address per tenant ({tenant}) ─────────────────────────────

    [Fact]
    public async Task ForgotPassword_ShouldMailTheLinkOnTheTenantsHost_WhenPublicBaseUrlNamesTheTenant()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = TenantPublicBaseUrl });
        using var request = ForgotPasswordRequest(TenantId);
        request.Headers.Host = ForgedHost;

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.SingleResetLink().Should().StartWith($"https://{TenantId}.example.test/reset-password?token=",
            because: "{tenant} is filled from the tenant the user was found in, never from the Host");
    }

    [Fact]
    public async Task ForgotPassword_ShouldMailTheLinkOnTheBrandingSubdomain_WhenTheTenantHasOne()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = TenantPublicBaseUrl });
        await factory.SeedBrandingSubdomainAsync("brand");
        using var request = ForgotPasswordRequest(TenantId);

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.SingleResetLink().Should().StartWith("https://brand.example.test/reset-password?token=",
            because: "a white-label tenant is reached at its branding subdomain, which resolves to it");
    }

    [Fact]
    public async Task OidcLogin_ShouldSendTheCallbackOnTheTenantsHostAsRedirectUri_WhenPublicBaseUrlNamesTheTenant()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = TenantPublicBaseUrl });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/oidc/login?tenant_id={TenantId}&return_url=%2F");
        request.Headers.Host = ForgedHost;

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"].ToString()
            .Should().Be($"https://{TenantId}.example.test{OidcCallbackPath}",
                because: "each tenant keeps the redirect URI it registered on its own host");
    }

    [Fact]
    public async Task OidcCallback_ShouldExchangeTheCodeWithTheCallbackOnTheTenantsHost_WhenPublicBaseUrlNamesTheTenant()
    {
        using var factory = new LinkFactory(new() { ["Platform:PublicBaseUrl"] = TenantPublicBaseUrl });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{OidcCallbackPath}?code=authorization-code");
        request.Headers.Host = ForgedHost;
        request.Headers.Add("Cookie", $"oidc_state={factory.ProtectFlowState()}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.ExchangeRedirectUris.Should().ContainSingle()
            .Which.Should().Be($"https://{TenantId}.example.test{OidcCallbackPath}",
                because: "the token request repeats the redirect_uri the authorization request sent");
    }

    // ─── Optional host filtering (ASP.NET Core's AllowedHosts) ───────────────

    [Theory]
    [InlineData("localhost")]          // the compose health checks: curl http://localhost:5000/health
    [InlineData("platform-api:5000")]  // Platform.Realtime's calls to Services:PlatformApi:BaseUrl
    [InlineData("10.42.0.17:5000")]    // a Kubernetes httpGet probe, whose Host is the pod IP
    public async Task Health_ShouldAnswer200_WhenAllowedHostsIsNotConfigured(string host)
    {
        using var factory = new LinkFactory(new());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the shipped configuration admits every Host, so internal callers keep working");
    }

    [Fact]
    public async Task Health_ShouldRefuseAnUnlistedHostWith400_WhenAllowedHostsIsConfigured()
    {
        using var factory = new LinkFactory(new() { ["AllowedHosts"] = "console.example.test;localhost;platform-api" });
        var client = factory.CreateClient();

        (await GetHealthAsync(client, ForgedHost)).Should().Be(HttpStatusCode.BadRequest);
        (await GetHealthAsync(client, "console.example.test")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(client, "localhost")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(client, "platform-api:5000")).Should().Be(HttpStatusCode.OK);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static HttpRequestMessage ForgotPasswordRequest(string tenantId) =>
        new(HttpMethod.Post, "/api/v1/auth/forgot-password")
        {
            Content = JsonContent.Create(new { tenantId, email = Email }),
        };

    private static async Task<HttpStatusCode> GetHealthAsync(HttpClient client, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// The real host, with configuration layered over the shipped appsettings (as an operator's
    /// environment variables would be), a known user, an OIDC-enabled tenant, and capturing stubs
    /// for the mail, the reset-token cache and the OIDC code exchange.
    /// </summary>
    private sealed class LinkFactory(Dictionary<string, string?> configuration) : WebApplicationFactory<Program>
    {
        public ConcurrentQueue<IReadOnlyDictionary<string, string>> TemplateVariables { get; } = new();
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();
        public ConcurrentQueue<string> StoredTokens { get; } = new();
        public ConcurrentQueue<string> ExchangeRedirectUris { get; } = new();

        public string SingleResetLink() =>
            TemplateVariables.Should().ContainSingle().Subject["ResetLink"];

        public ValueTask SeedBrandingSubdomainAsync(string subdomain) =>
            Services.GetRequiredService<ITenantBrandingStore>().UpsertAsync(
                new TenantBranding { TenantId = TenantId, Subdomain = subdomain });

        public string ProtectFlowState()
        {
            var state = new OidcFlowState
            {
                CodeVerifier = "code-verifier",
                Nonce = "nonce",
                TenantId = TenantId,
                ReturnUrl = "/",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            };
            var protector = Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("OidcFlowState");
            return protector.Protect(JsonSerializer.Serialize(state, OidcJsonContext.Default.OidcFlowState));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(configuration));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                var userStore = Substitute.For<IUserStore>();
                userStore.GetByEmailAsync(new TenantId(TenantId), Email, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<User?>(new User
                    {
                        UserId = EntityId.From(UserId),
                        TenantId = new TenantId(TenantId),
                        Email = Email,
                        DisplayName = "Victim",
                        Role = UserRole.Admin,
                        Status = UserStatus.Active,
                        CreatedAt = DateTimeOffset.UtcNow,
                    }));
                Replace(services, userStore);

                var authConfigStore = Substitute.For<ITenantAuthConfigStore>();
                authConfigStore.GetAsync(TenantId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig
                    {
                        TenantId = TenantId,
                        OidcEnabled = true,
                        OidcAuthority = IdpAuthority,
                        OidcClientId = "verbara-console",
                        OidcClientSecret = "client-secret",
                    }));
                Replace(services, authConfigStore);

                var resetCache = Substitute.For<IPasswordResetCache>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
                resetCache.StoreAsync(Arg.Do<string>(StoredTokens.Enqueue), Arg.Any<PasswordResetEntry>(), Arg.Any<CancellationToken>())
                    .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012
                Replace(services, resetCache);

                var tokenExchange = Substitute.For<IOidcTokenExchangeService>();
                tokenExchange.ExchangeCodeAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Do<string>(ExchangeRedirectUris.Enqueue), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<CancellationToken>())
                    .Returns<Task<OidcTokenResponse>>(_ => throw new InvalidOperationException("exchange stubbed out"));
                Replace(services, tokenExchange);

                Replace<IEmailService>(services, new CapturingEmail(Sent));

                var templates = Substitute.For<IEmailTemplateService>();
                templates.Render(Arg.Any<string>(), Arg.Any<BrandingContext>(),
                        Arg.Do<IReadOnlyDictionary<string, string>>(v => TemplateVariables.Enqueue(new Dictionary<string, string>(v))))
                    .Returns("<html/>");
                Replace(services, templates);
            });

            var host = base.CreateHost(builder);
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TenantId);
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, ForgedHostTenant);
            return host;
        }

        private static void Replace<T>(IServiceCollection services, T instance) where T : class
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T) && !d.IsKeyedService).ToList())
                services.Remove(descriptor);
            services.AddSingleton(instance);
        }
    }

    private sealed class CapturingEmail(ConcurrentQueue<EmailMessage> sink) : IEmailService
    {
        public ValueTask SendAsync(EmailMessage message, CancellationToken ct)
        {
            sink.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }
}
