using Verbara.Platform.Api.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Without a usable public address the API sends no password-reset email and refuses OIDC sign-in, and
/// each refusal logs a warning when it happens. An installation upgraded without the setting would learn
/// of it only from users whose email never arrives, so the host also says so once when it starts, naming
/// the setting and why no address resolves. These tests start the real host and capture its records.
/// </summary>
public sealed class PublicBaseUrlStartupTests
{
    private const int NoPublicBaseUrlEventId = 7522;

    [Theory]
    [InlineData(null, "https://a.example.test,https://b.example.test", "CORS_ORIGINS names 2 origins")]
    [InlineData(null, "*", "CORS_ORIGINS is '*'")]
    [InlineData(null, null, "neither is CORS_ORIGINS")]
    [InlineData(null, "app.example.test", "CORS_ORIGINS' one origin is not an absolute http(s) URL")]
    [InlineData("console.example.test", "https://app.example.test", "Platform:PublicBaseUrl is not an absolute http(s) URL")]
    [InlineData("https://{tenant}.example.test/?x=1", null, "Platform:PublicBaseUrl is not an absolute http(s) URL")]
    public void Startup_ShouldWarnOnceThatResetLinksAndSignOnAreOff_WhenNoPublicBaseUrlResolves(
        string? publicBaseUrl, string? corsOrigins, string reason)
    {
        var capture = new LogRecordCapture();
        using (var factory = new OidcSignInApiFactory(capture: capture, configuration: new Dictionary<string, string?>
        {
            ["Platform:PublicBaseUrl"] = publicBaseUrl,
            ["CORS_ORIGINS"] = corsOrigins,
        }))
        {
            _ = factory.Services; // starts the host
        }

        var warning = capture.Records.Where(r => r.EventId.Id == NoPublicBaseUrlEventId).Should().ContainSingle(
            because: "the host says once, at start, that reset links and single sign-on are off").Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("Platform:PublicBaseUrl").And.Contain(reason);
    }

    [Theory]
    [InlineData("https://console.example.test", null)]
    [InlineData(null, "https://app.example.test")]
    [InlineData("https://{tenant}.example.test", "https://a.example.test,https://b.example.test")]
    public void Startup_ShouldNotWarn_WhenAPublicBaseUrlResolves(string? publicBaseUrl, string? corsOrigins)
    {
        var capture = new LogRecordCapture();
        using (var factory = new OidcSignInApiFactory(capture: capture, configuration: new Dictionary<string, string?>
        {
            ["Platform:PublicBaseUrl"] = publicBaseUrl,
            ["CORS_ORIGINS"] = corsOrigins,
        }))
        {
            _ = factory.Services;
        }

        capture.Records.Should().Contain(
            r => r.Category.StartsWith("Microsoft.Hosting.Lifetime", StringComparison.Ordinal)
                || r.Category.StartsWith("Verbara", StringComparison.Ordinal),
            because: "the capture must see the records written while the host started");
        capture.Records.Should().NotContain(r => r.EventId.Id == NoPublicBaseUrlEventId);
    }
}
