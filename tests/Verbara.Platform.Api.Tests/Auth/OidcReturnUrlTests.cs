namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Where an OIDC sign-in sends the browser when it completes. The callback appends its result to the
/// <c>return_url</c> the sign-in started with, in the URL fragment: the access token, the MFA challenge
/// token, or the enrollment notice with the user's email. Whoever sends a user a link to the console's own
/// login endpoint chooses that <c>return_url</c>, so it is honoured only on the console's origin: a path,
/// or an absolute URL whose scheme, host and port are those of the configured public address. Anything
/// else is replaced by <c>/</c>. These tests run the real host pipeline.
/// </summary>
public sealed class OidcReturnUrlTests
{
    [Theory]
    [InlineData("https://attacker.example/steal")]
    [InlineData("//attacker.example/steal")]
    [InlineData("/\\attacker.example/steal")]
    [InlineData("/\t/attacker.example/steal")]
    [InlineData(" //attacker.example/steal")]
    [InlineData("https://console.example.test.attacker.example/")] // begins with the configured address
    [InlineData("https://console.example.test@attacker.example/")]
    [InlineData("http://console.example.test/login")]
    [InlineData("https://console.example.test:8443/login")]
    [InlineData("javascript:alert(document.domain)")]
    public async Task OidcCallback_ShouldSendTheAccessTokenToTheConsoleRoot_WhenReturnUrlIsNotOnTheConsoleOrigin(string returnUrl)
    {
        using var factory = new OidcSignInApiFactory();

        var location = await factory.SignInAsync(returnUrl);

        location.Should().StartWith("/#oidc_callback&access_token=",
            because: $"the access token must stay on the console, whatever return_url ('{returnUrl}') the link named");
    }

    [Theory]
    [InlineData("/login", "/login")]
    [InlineData("/admin/tenants/acme", "/admin/tenants/acme")]
    [InlineData("https://console.example.test/login", "https://console.example.test/login")]
    [InlineData("https://CONSOLE.example.test:443/login", "https://console.example.test/login")]
    public async Task OidcCallback_ShouldSendTheAccessTokenToReturnUrl_WhenItIsOnTheConsoleOrigin(string returnUrl, string expected)
    {
        using var factory = new OidcSignInApiFactory();

        var location = await factory.SignInAsync(returnUrl);

        location.Should().StartWith($"{expected}#oidc_callback&access_token=",
            because: "the console's own pages, as a path or on its own origin, are where a sign-in returns to");
    }

    [Fact]
    public async Task OidcCallback_ShouldSendTheMfaChallengeTokenToTheConsoleRoot_WhenReturnUrlIsAnotherOrigin()
    {
        using var factory = new OidcSignInApiFactory(mfaEnabled: true);

        var location = await factory.SignInAsync("https://attacker.example/steal");

        location.Should().StartWith("/#oidc_mfa_challenge&challenge_token=",
            because: "the challenge token completes the sign-in with a second factor and must stay on the console");
    }

    [Fact]
    public async Task OidcCallback_ShouldSendTheEnrollmentNoticeToTheConsoleRoot_WhenReturnUrlIsAnotherOrigin()
    {
        using var factory = new OidcSignInApiFactory(policyRequiresMfa: true);

        var location = await factory.SignInAsync("https://attacker.example/steal");

        location.Should().StartWith("/#oidc_mfa_enrollment_required&",
            because: "the notice carries the user's email and must stay on the console");
    }

    [Theory]
    [InlineData("https://oidc-signin-tenant.example.test/login", "https://oidc-signin-tenant.example.test/login")]
    [InlineData("https://other-tenant.example.test/login", "/")]
    public async Task OidcCallback_ShouldHoldReturnUrlToTheTenantsHost_WhenPublicBaseUrlNamesTheTenant(string returnUrl, string expected)
    {
        using var factory = new OidcSignInApiFactory(configuration: new Dictionary<string, string?>
        {
            ["Platform:PublicBaseUrl"] = "https://{tenant}.example.test",
        });

        var location = await factory.SignInAsync(returnUrl);

        location.Should().StartWith($"{expected}#oidc_callback&access_token=",
            because: "the console of the tenant signing in is its own host, and another tenant's host is another origin");
    }

    [Fact]
    public async Task OidcCallback_ShouldSendTheAccessTokenToTheConsoleRoot_WhenTheFlowStateCarriesAnotherOrigin()
    {
        // A state cookie issued before the login endpoint checked return_url, and still valid for
        // its five minutes, carries the value as it was sent: the callback checks it again.
        using var factory = new OidcSignInApiFactory();

        var location = await factory.CompleteWithFlowStateAsync("https://attacker.example/steal");

        location.Should().StartWith("/#oidc_callback&access_token=");
    }
}
