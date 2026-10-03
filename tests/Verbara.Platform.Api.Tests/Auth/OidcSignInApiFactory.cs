using System.Net;
using System.Text.Json;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The real host with one OIDC-enabled tenant whose identity provider vouches for one user. Only the
/// provider's side is stubbed (the code exchange, the ID-token validation) and the provisioning of the
/// user; the login endpoint, the state cookie, the callback, the MFA gate and token issuance run as
/// shipped. Configuration is layered over the shipped appsettings, as an operator's environment
/// variables would be.
/// </summary>
internal sealed class OidcSignInApiFactory(
    bool mfaEnabled = false,
    bool policyRequiresMfa = false,
    LogRecordCapture? capture = null,
    IReadOnlyDictionary<string, string?>? configuration = null) : WebApplicationFactory<Program>
{
    public const string TenantId = "oidc-signin-tenant";
    public const string PublicBaseUrl = "https://console.example.test";
    private const string IdpAuthority = "https://idp.example.test";
    private const string StateCookie = "oidc_state";

    public User User { get; } = new()
    {
        UserId = EntityId.From("oidc-signin-user"),
        TenantId = new TenantId(TenantId),
        Email = "user@oidc-signin.test",
        DisplayName = "Signed In",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        MfaEnabled = mfaEnabled,
        MfaSecret = mfaEnabled ? "JBSWY3DPEHPK3PXP" : null,
        MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
    };

    /// <summary>
    /// Starts a sign-in with <paramref name="returnUrl"/> as the console does, then completes it the way
    /// the identity provider's redirect does, carrying the state cookie the login set. Returns where the
    /// callback sends the browser.
    /// </summary>
    public async Task<string> SignInAsync(string returnUrl)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var login = await client.GetAsync(
            $"/api/v1/auth/oidc/login?tenant_id={TenantId}&return_url={Uri.EscapeDataString(returnUrl)}");
        login.StatusCode.Should().Be(HttpStatusCode.Redirect, await login.Content.ReadAsStringAsync());
        var stateCookie = login.Headers.GetValues("Set-Cookie")
            .Select(cookie => cookie.Split(';')[0])
            .Single(cookie => cookie.StartsWith($"{StateCookie}=", StringComparison.Ordinal));

        return await CallBackAsync(client, stateCookie);
    }

    /// <summary>
    /// Completes a sign-in whose encrypted flow state carries <paramref name="returnUrl"/> as is, as a
    /// state cookie issued before the login endpoint checked it would. Returns where the callback sends
    /// the browser.
    /// </summary>
    public Task<string> CompleteWithFlowStateAsync(string returnUrl)
    {
        var state = new OidcFlowState
        {
            CodeVerifier = "code-verifier",
            Nonce = "nonce",
            TenantId = TenantId,
            ReturnUrl = returnUrl,
            ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        };
        var protector = Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("OidcFlowState");
        var cookie = $"{StateCookie}={protector.Protect(JsonSerializer.Serialize(state, OidcJsonContext.Default.OidcFlowState))}";

        return CallBackAsync(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), cookie);
    }

    private static async Task<string> CallBackAsync(HttpClient client, string stateCookie)
    {
        using var callback = new HttpRequestMessage(HttpMethod.Get, "/api/auth/oidc/callback?code=authorization-code&state=state");
        callback.Headers.Add("Cookie", stateCookie);
        using var response = await client.SendAsync(callback);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return response.Headers.Location!.OriginalString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?> { ["Platform:PublicBaseUrl"] = PublicBaseUrl };
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings));
        if (capture is not null)
            builder.ConfigureLogging(logging => logging.AddProvider(capture));
    }

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

            var tokenExchange = Substitute.For<IOidcTokenExchangeService>();
            tokenExchange.ExchangeCodeAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new OidcTokenResponse { IdToken = "id-token", AccessToken = "provider-access-token" }));
            tokenExchange.ValidateIdTokenAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new OidcClaimsResult("provider-subject", User.Email, User.DisplayName, true)));
            Replace(services, tokenExchange);

            var provisioning = Substitute.For<IOidcUserProvisioningService>();
            provisioning.ProvisionOrUpdateAsync(
                    Arg.Any<string>(), Arg.Any<OidcClaimsResult>(), Arg.Any<TenantAuthConfig>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<User?>(User));
            Replace(services, provisioning);

            var mfaPolicy = Substitute.For<IMfaPolicyEvaluator>();
            mfaPolicy.RequiresMfaAsync(Arg.Any<string>(), Arg.Any<UserRole>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(policyRequiresMfa));
            Replace(services, mfaPolicy);
        });

        var host = base.CreateHost(builder);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, "localhost");
        return host;
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T) && !d.IsKeyedService).ToList())
            services.Remove(descriptor);
        services.AddSingleton(instance);
    }
}
