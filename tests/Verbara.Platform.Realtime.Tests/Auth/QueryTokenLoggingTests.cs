using System.Net;
using Verbara.Platform.Realtime.Tests.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Tests.Auth;

/// <summary>
/// A browser WebSocket cannot carry an Authorization header, so the SignalR client puts the access token
/// in the query of every negotiate and connect request. The real Realtime host must never write that
/// query to a log record: whoever reads the logs would otherwise hold a token that is valid right now.
/// </summary>
public sealed class QueryTokenLoggingTests : IClassFixture<RealtimeHostFixture>
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    private readonly RealtimeHostFixture _host;

    public QueryTokenLoggingTests(RealtimeHostFixture host) => _host = host;

    [Fact]
    public async Task Negotiate_ShouldKeepTheAccessTokenOutOfEveryLogRecord_WhenTheTokenTravelsInTheQuery()
    {
        var token = _host.MintAccessToken("acme", NewUserId(), DateTimeOffset.UtcNow.AddMinutes(15));

        await NegotiateAsync(_host, token);

        _host.Logs.Records.Should().Contain(
            record => record.ScopeValues.Contains("RequestPath=/hubs/platform/negotiate"),
            because: "the capture must see the records written while the negotiate request ran");
        _host.Logs.RecordsContaining(token).Should().BeEmpty(
            because: "a token in a log record can be replayed by anyone who reads the logs");
    }

    [Fact]
    public async Task Negotiate_ShouldKeepTheAccessTokenOutOfLogs_WhenConfigurationRaisesAspNetCoreLoggingToTrace()
    {
        // The settings an operator reaches for while troubleshooting, including the exact category the
        // host floors. Only a code-level floor survives them, which is what this test pins.
        using var verbose = _host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
            })));
        await _host.TrustSigningKeyAsync(verbose.Services);
        var token = _host.MintAccessToken("acme", NewUserId(), DateTimeOffset.UtcNow.AddMinutes(15));

        await NegotiateAsync(verbose, token);

        _host.Logs.Records.Should().Contain(
            record => record.Level <= LogLevel.Debug
                && record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                && record.ScopeValues.Contains("RequestPath=/hubs/platform/negotiate"),
            because: "the configuration must have raised ASP.NET Core's own request-time logging");
        _host.Logs.RecordsContaining(token).Should().BeEmpty(
            because: "no ASP.NET Core category, at any level configuration can set, may write the query");
    }

    /// <summary>
    /// Negotiates with the token in the query, as the browser client does, and proves the token is what
    /// authenticated the request: the same negotiate without it is refused.
    /// </summary>
    private static async Task NegotiateAsync(WebApplicationFactory<Program> host, string token)
    {
        using var client = host.CreateClient();
        using var timeout = new CancellationTokenSource(GuardTimeout);

        using var anonymous = await client.PostAsync(
            new Uri("/hubs/platform/negotiate?negotiateVersion=1", UriKind.Relative), content: null, timeout.Token);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, because: "the hub requires an authenticated caller");

        using var response = await client.PostAsync(
            new Uri($"/hubs/platform/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative),
            content: null, timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "the token in the query authenticates the request");
    }

    private static string NewUserId() => $"query-token-user-{Guid.NewGuid():N}";
}
