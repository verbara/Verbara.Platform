using System.Net;
using Verbara.Platform.Api.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Tests.Logging;

/// <summary>
/// The browser cannot put an Authorization header on an EventSource or a WebSocket, so the SSE stream
/// and the SignalR hub take the access token from the query string. A query string must therefore never
/// reach a log record: whoever can read the logs would otherwise hold a token that is valid right now
/// for every user who is online. These tests run the real Program.cs and capture every record it writes.
/// </summary>
public sealed class QueryTokenLoggingTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StreamEvents_ShouldKeepTheAccessTokenOutOfEveryLogRecord_WhenTheTokenTravelsInTheQuery()
    {
        var capture = new LogRecordCapture();
        string token;
        using (var factory = new LogCapturingApiFactory(capture))
        {
            token = MintCustomerAdminToken(factory);
            await OpenStreamThenCloseItAsync(factory, $"/api/v1/events/stream?token={token}");
        } // disposing the host lets the request finish, so its last records are written too

        capture.Records.Should().Contain(
            record => record.ScopeValues.Contains("RequestPath=/api/v1/events/stream"),
            because: "the capture must see the records written while the stream request ran");
        capture.RecordsContaining(token).Should().BeEmpty(
            because: "a token in a log record can be replayed by anyone who reads the logs");
    }

    [Theory]
    [InlineData("/api/v1/recordings/rec-1/stream?token={0}")]
    [InlineData("/hubs/platform?id=connection-1&access_token={0}")]
    [InlineData("/api/v1/auth/oidc/callback?code={0}&state=state-1")]
    [InlineData("/api/v1/webhooks/acct-status-customer/whatsapp?hub.mode=subscribe&hub.verify_token={0}&hub.challenge=c")]
    [InlineData("/api/v1/agents/me?access_token={0}")]
    public async Task RequestLog_ShouldNotContainTheQueryCredential_WhenAnyPathCarriesOne(string pathAndQuery)
    {
        // Shaped like a JWT so the query-token paths hand it to JwtBearer; the outcome of each request
        // does not matter, only what the host writes about it.
        var credential = $"eyJ{Guid.NewGuid():N}";
        var target = string.Format(System.Globalization.CultureInfo.InvariantCulture, pathAndQuery, credential);
        var capture = new LogRecordCapture();
        using (var factory = new LogCapturingApiFactory(capture))
        {
            using var client = factory.CreateClient();
            using var timeout = new CancellationTokenSource(GuardTimeout);
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }

        var path = target[..target.IndexOf('?', StringComparison.Ordinal)];
        capture.Records.Should().Contain(
            record => record.ScopeValues.Contains($"RequestPath={path}"),
            because: "the capture must see the records written while the request ran");
        capture.RecordsContaining(credential).Should().BeEmpty(
            because: "the request log records the query of every request, whichever path carries it");
    }

    [Fact]
    public async Task StreamEvents_ShouldKeepTheAccessTokenOutOfLogs_WhenConfigurationRaisesAspNetCoreLoggingToTrace()
    {
        // The settings an operator reaches for while troubleshooting, including the exact category the
        // host floors. Configuration must not be able to bring the request log back: only a code-level
        // floor survives it, which is what this test pins.
        var capture = new LogRecordCapture();
        string token;
        using (var factory = new LogCapturingApiFactory(capture, new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
        }))
        {
            token = MintCustomerAdminToken(factory);
            await OpenStreamThenCloseItAsync(factory, $"/api/v1/events/stream?token={token}");
        }

        capture.Records.Should().Contain(
            record => record.Level <= LogLevel.Debug
                && record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                && record.ScopeValues.Contains("RequestPath=/api/v1/events/stream"),
            because: "the configuration must have raised ASP.NET Core's own request-time logging");
        capture.RecordsContaining(token).Should().BeEmpty(
            because: "no ASP.NET Core category, at any level configuration can set, may write the query");
    }

    private static string MintCustomerAdminToken(AccountStatusApiFactory factory)
    {
        var user = factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)
            ?? throw new InvalidOperationException("The factory seeds the customer admin.");
        return factory.MintAccessToken(user);
    }

    /// <summary>
    /// Opens the stream with the token in the query (as the browser's EventSource does), proves it was
    /// accepted, and closes it.
    /// </summary>
    private static async Task OpenStreamThenCloseItAsync(AccountStatusApiFactory factory, string pathAndQuery)
    {
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var response = await client.GetAsync(pathAndQuery, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "the token in the query authenticates the stream");
    }
}

/// <summary>
/// The account-status host with a log capture attached, and optionally extra configuration layered over
/// the shipped appsettings (as an operator's environment variables would be).
/// </summary>
internal sealed class LogCapturingApiFactory(
    LogRecordCapture capture, IReadOnlyDictionary<string, string?>? configuration = null) : AccountStatusApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (configuration is not null)
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(configuration));
        builder.ConfigureLogging(logging => logging.AddProvider(capture));
    }
}
