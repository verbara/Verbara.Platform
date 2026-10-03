using Verbara.Platform.Api.Endpoints.Shared;
using Microsoft.Extensions.Configuration;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Which configured value <see cref="PublicBaseUrl.Resolve"/> settles on, and which it refuses. The
/// request is never an input: the end-to-end behaviour under a forged Host is in
/// <see cref="PublicBaseUrlLinkTests"/>.
/// </summary>
public sealed class PublicBaseUrlTests
{
    [Theory]
    [InlineData("https://console.example.test", "https://console.example.test")]
    [InlineData("https://console.example.test/", "https://console.example.test")]
    [InlineData("  https://console.example.test  ", "https://console.example.test")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("HTTPS://Console.Example.Test:8443", "https://console.example.test:8443")]
    [InlineData("https://example.test/console/", "https://example.test/console")]
    public void Resolve_ShouldReturnTheNormalizedSetting_WhenPublicBaseUrlIsAnAbsoluteHttpUrl(string configured, string expected)
    {
        var resolved = PublicBaseUrl.Resolve(Configuration(configured, corsOrigins: null));

        resolved.Should().Be(expected);
    }

    [Fact]
    public void Resolve_ShouldPreferPublicBaseUrl_WhenCorsOriginsAlsoNamesOneOrigin()
    {
        var resolved = PublicBaseUrl.Resolve(Configuration("https://console.example.test", "https://app.example.test"));

        resolved.Should().Be("https://console.example.test");
    }

    [Theory]
    [InlineData("console.example.test")]
    [InlineData("/reset-password")]
    [InlineData("ftp://console.example.test")]
    [InlineData("https://console.example.test/?tenant=acme")]
    [InlineData("https://console.example.test/#top")]
    [InlineData("https://user:secret@console.example.test")]
    public void Resolve_ShouldReturnNull_WhenPublicBaseUrlIsSetButUnusable(string configured)
    {
        // A usable single CORS origin is present on purpose: an operator who set an address meant
        // that address, so the setting is not silently replaced by another origin.
        var resolved = PublicBaseUrl.Resolve(Configuration(configured, "https://app.example.test"));

        resolved.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "https://app.example.test", "https://app.example.test")]
    [InlineData("", "https://app.example.test", "https://app.example.test")]
    [InlineData("   ", " https://app.example.test/ ", "https://app.example.test")]
    [InlineData(null, "https://app.example.test,", "https://app.example.test")]
    public void Resolve_ShouldReturnTheSingleCorsOrigin_WhenPublicBaseUrlIsNotSet(
        string? configured, string corsOrigins, string expected)
    {
        var resolved = PublicBaseUrl.Resolve(Configuration(configured, corsOrigins));

        resolved.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("https://a.example.test,https://b.example.test")]
    [InlineData("app.example.test")]
    public void Resolve_ShouldReturnNull_WhenNeitherSettingNamesOneUsableAddress(string? corsOrigins)
    {
        var resolved = PublicBaseUrl.Resolve(Configuration(configured: null, corsOrigins));

        resolved.Should().BeNull();
    }

    [Fact]
    public void ResetPasswordLink_ShouldPointAtTheConsoleResetPage_WhenGivenABaseUrl()
    {
        var link = PublicBaseUrl.ResetPasswordLink("https://example.test/console", "token123");

        link.Should().Be("https://example.test/console/reset-password?token=token123");
    }

    [Fact]
    public void ResetPasswordLink_ShouldPercentEncodeTheToken_WhenItHoldsBase64ReservedCharacters()
    {
        var link = PublicBaseUrl.ResetPasswordLink("https://console.example.test", "ab+c/d=");

        link.Should().Be("https://console.example.test/reset-password?token=ab%2Bc%2Fd%3D");
    }

    [Fact]
    public void OidcRedirectUri_ShouldBeTheUnversionedCallback_WhenGivenABaseUrl()
    {
        var redirectUri = PublicBaseUrl.OidcRedirectUri("https://console.example.test");

        redirectUri.Should().Be("https://console.example.test/api/auth/oidc/callback",
            because: "identity providers hold this exact value as the registered redirect URI");
    }

    private static IConfiguration Configuration(string? configured, string? corsOrigins) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:PublicBaseUrl"] = configured,
                ["CORS_ORIGINS"] = corsOrigins,
            })
            .Build();
}
