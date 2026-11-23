using System.Net;
using Verbara.Platform.Identity.Auth.Jwt;
using Verbara.Platform.Realtime.Tests.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Realtime.Tests.Auth;

/// <summary>
/// The real Realtime host, through its own JwtBearer <c>OnMessageReceived</c> event: a host that read
/// the key store while it was empty (a fresh install before setup) accepts the admin's first
/// live-update connect signed with the first key, on the first request.
/// </summary>
/// <remarks>
/// Needs Docker, as every <see cref="RealtimeHostFixture"/> suite does: Program.cs requires
/// <c>ConnectionStrings:Cluster</c> and runs the cluster-lock migration against it before it serves,
/// and a test host cannot skip top-level statements.
/// </remarks>
public sealed class FirstSigningKeyRealtimeHostTests : IClassFixture<RealtimeHostFixture>
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    private readonly RealtimeHostFixture _host;

    public FirstSigningKeyRealtimeHostTests(RealtimeHostFixture host) => _host = host;

    [Fact]
    public async Task Negotiate_ShouldAcceptTheFirstKeysToken_WhenSetupCreatesItAfterTheHostReadTheStoreEmpty()
    {
        // A host of its own (its own Program, its own in-memory key store), not the fixture's seeded one.
        using var fresh = _host.WithWebHostBuilder(_ => { });
        (await fresh.Services.GetRequiredService<IJwtKeyStore>().GetAllAsync())
            .Should().BeEmpty(because: "a fresh install holds no signing key before setup");
        using var client = fresh.CreateClient();
        using var timeout = new CancellationTokenSource(GuardTimeout);

        // Before setup, connects reach the host: its keys load, empty.
        (await NegotiateAsync(client, token: null, timeout.Token)).Should().Be(HttpStatusCode.Unauthorized);

        // Setup creates the first key; the admin's live updates connect with a token signed with it.
        await _host.TrustSigningKeyAsync(fresh.Services);
        var token = _host.MintAccessToken("acme", $"first-key-user-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddMinutes(15));

        (await NegotiateAsync(client, token, timeout.Token)).Should().Be(HttpStatusCode.OK,
            because: "the admin's first live-update connect after setup must not be refused");
    }

    private static async Task<HttpStatusCode> NegotiateAsync(HttpClient client, string? token, CancellationToken cancellationToken)
    {
        var query = token is null ? "" : $"&access_token={token}";
        using var response = await client.PostAsync(
            new Uri($"/hubs/platform/negotiate?negotiateVersion=1{query}", UriKind.Relative), content: null, cancellationToken);
        return response.StatusCode;
    }
}
