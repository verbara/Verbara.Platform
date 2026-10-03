using Verbara.Platform.Api.Tests.Auth;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Tests.Logging;

/// <summary>
/// The OIDC callback hands its result to the browser in the Location of a redirect: the access token, or
/// the MFA challenge token, in the fragment. ASP.NET Core's redirect result logs the destination of every
/// redirect, so no log record may carry it: whoever reads the logs would hold a token that signs in as the
/// user. These tests run the real Program.cs and capture every record it writes.
/// </summary>
public sealed class RedirectLoggingTests
{
    [Fact]
    public async Task OidcCallback_ShouldKeepTheIssuedAccessTokenOutOfEveryLogRecord_WhenSignInSucceeds()
    {
        var capture = new LogRecordCapture();
        string location;
        using (var factory = new OidcSignInApiFactory(capture: capture))
            location = await factory.SignInAsync("/login");

        var token = FragmentValue(location, "access_token");
        token.Should().StartWith("eyJ", because: "the sign-in must have issued an access token");
        capture.Records.Should().Contain(
            record => record.ScopeValues.Contains("RequestPath=/api/auth/oidc/callback"),
            because: "the capture must see the records written while the callback ran");
        capture.RecordsContaining(token).Should().BeEmpty(
            because: "an access token in a log record can be replayed by anyone who reads the logs");
    }

    [Fact]
    public async Task OidcCallback_ShouldKeepTheMfaChallengeTokenOutOfEveryLogRecord_WhenTheUserHasMfa()
    {
        var capture = new LogRecordCapture();
        string location;
        using (var factory = new OidcSignInApiFactory(mfaEnabled: true, capture: capture))
            location = await factory.SignInAsync("/login");

        // The token is Base64, so the Location carries it percent-encoded: look for both spellings.
        var encoded = FragmentValue(location, "challenge_token");
        encoded.Should().NotBeEmpty(because: "the sign-in must have issued an MFA challenge");
        capture.RecordsContaining(encoded).Should().BeEmpty(
            because: "the challenge token, with the second factor, completes the sign-in");
        capture.RecordsContaining(Uri.UnescapeDataString(encoded)).Should().BeEmpty();
    }

    [Fact]
    public async Task OidcCallback_ShouldKeepTheIssuedAccessTokenOutOfLogs_WhenConfigurationRaisesAspNetCoreLoggingToTrace()
    {
        // The settings an operator reaches for while troubleshooting, including the exact category the
        // host floors. Only a code-level floor survives them, which is what this test pins.
        var capture = new LogRecordCapture();
        string location;
        using (var factory = new OidcSignInApiFactory(capture: capture, configuration: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Http.Result"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Http.Result.RedirectResult"] = "Information",
        }))
        {
            location = await factory.SignInAsync("/login");
        }

        var token = FragmentValue(location, "access_token");
        capture.Records.Should().Contain(
            record => record.Level <= LogLevel.Debug
                && record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                && record.ScopeValues.Contains("RequestPath=/api/auth/oidc/callback"),
            because: "the configuration must have raised ASP.NET Core's own request-time logging");
        capture.RecordsContaining(token).Should().BeEmpty(
            because: "no ASP.NET Core category, at any level configuration can set, may write a redirect's destination");
    }

    /// <summary>The value of <paramref name="name"/> in the Location's fragment, as the Location spells it.</summary>
    private static string FragmentValue(string location, string name)
    {
        var fragment = location[(location.IndexOf('#', StringComparison.Ordinal) + 1)..];
        var pair = fragment.Split('&').Single(p => p.StartsWith($"{name}=", StringComparison.Ordinal));
        return pair[(name.Length + 1)..];
    }
}
