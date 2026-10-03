using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Mail.Tests.Hosting;

/// <summary>
/// Microsoft's OAuth redirect hands the authorization code to <c>/auth/microsoft/callback</c> in the
/// query string. The real Mail host must never write a query string to a log record: these tests run its
/// Program.cs and capture every record it writes for such a request.
/// </summary>
public sealed class QueryCredentialLoggingTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task MicrosoftCallback_ShouldKeepTheAuthorizationCodeOutOfEveryLogRecord_WhenItTravelsInTheQuery()
    {
        var code = NewCode();
        var capture = new LogRecordCapture();
        using (var factory = new LogCapturingMailFactory(capture))
            await CallBackAsync(factory, code);

        capture.Records.Should().Contain(
            record => record.ScopeValues.Contains("RequestPath=/auth/microsoft/callback"),
            because: "the capture must see the records written while the callback request ran");
        capture.RecordsContaining(code).Should().BeEmpty(
            because: "a credential in a log record can be used by anyone who reads the logs");
    }

    [Fact]
    public async Task MicrosoftCallback_ShouldKeepTheAuthorizationCodeOutOfLogs_WhenConfigurationRaisesAspNetCoreLoggingToTrace()
    {
        // The settings an operator reaches for while troubleshooting, including the exact category the
        // host floors. Only a code-level floor survives them, which is what this test pins.
        var code = NewCode();
        var capture = new LogRecordCapture();
        using (var factory = new LogCapturingMailFactory(capture, new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
        }))
        {
            await CallBackAsync(factory, code);
        }

        capture.Records.Should().Contain(
            record => record.Level <= LogLevel.Debug
                && record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                && record.ScopeValues.Contains("RequestPath=/auth/microsoft/callback"),
            because: "the configuration must have raised ASP.NET Core's own request-time logging");
        capture.RecordsContaining(code).Should().BeEmpty(
            because: "no ASP.NET Core category, at any level configuration can set, may write the query");
    }

    private static string NewCode() => $"M.C{Guid.NewGuid():N}";

    /// <summary>Calls the OAuth callback the way Microsoft's redirect does: the code in the query.</summary>
    private static async Task CallBackAsync(WebApplicationFactory<Program> factory, string code)
    {
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var response = await client.GetAsync(
            new Uri($"/auth/microsoft/callback?code={code}&state=state-1", UriKind.Relative), timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: "the callback ran and found no state cookie");
    }
}

/// <summary>
/// The real Mail host with a log capture attached, and optionally extra configuration layered over its
/// defaults (as an operator's environment variables would be). It gets the two settings the host needs
/// to start, neither of which these requests use: a Postgres connection string (Program.cs registers the
/// token store only when one is configured, and the token-refresh worker needs that store) and the CSAT
/// reply-token secret (the IMAP poller's handler is built at startup even with IMAP disabled).
/// </summary>
internal sealed class LogCapturingMailFactory(
    LogRecordCapture capture, IReadOnlyDictionary<string, string?>? configuration = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (configuration is not null)
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(configuration));
        builder.ConfigureLogging(logging => logging.AddProvider(capture));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration reaches the entry point as arguments, so Program.cs sees it while composing.
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] =
                "Host=127.0.0.1;Port=1;Database=mail_tests;Username=mail_tests;Password=mail_tests;Timeout=1",
            ["Imap:TokenSigningSecret"] = "mail-host-logging-tests-reply-token-secret",
        }));
        return base.CreateHost(builder);
    }
}
