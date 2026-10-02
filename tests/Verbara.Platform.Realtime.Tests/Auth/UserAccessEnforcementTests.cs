using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Verbara.Platform.Core;
using Verbara.Platform.Realtime.Auth;
using Verbara.Platform.Realtime.Clients;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Verbara.Platform.Realtime.Tests.Auth;

/// <summary>
/// End to end over a real SignalR connection: the production registration
/// (<see cref="UserAccessEnforcementServiceCollectionExtensions.AddUserAccessEnforcement"/>) on a
/// Kestrel host, a real client, a real push bus. Proves what the unit tests cannot: the global hub
/// filter actually runs on connect, its refusal closes the client, a revocation published on the
/// bus drops a live client, and a token's expiry closes the connection in a way the client
/// reconnects from. The token-expiry timer runs on a fake clock; SignalR's own timers do not.
/// </summary>
public sealed class UserAccessEnforcementTests : IAsyncLifetime
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly SwitchableStatusClient _status = new();
    private readonly FakeTimeProvider _time = new(Now);
    private WebApplication _app = null!;
    private string _hubUrl = null!;


    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IUserAccessStatusClient>(_status);
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddVerbaraPush();
        builder.Services.AddUserAccessEnforcement();
        builder.Services
            .AddAuthentication(TokenAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(TokenAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapHub<ProbeHub>("/hubs/probe");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        _hubUrl = $"{address}/hubs/probe";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Connect_ShouldBeClosedByTheServer_WhenAccountIsNotActive()
    {
        _status.Verdict = UserAccessVerdict.Denied;
        await using var connection = Connect(new AccessToken("acme", "alice", Now + TokenLifetime));
        var closed = ClosedSignal(connection);

        // The refusal can surface either as a failed start or as a close right after the
        // handshake; both end at the Closed assertion below.
        _ = await Record.ExceptionAsync(() => connection.StartAsync());

        var error = await closed.WaitAsync(GuardTimeout);
        error.Should().NotBeNull();
        error!.Message.Should().Contain("Account is not active");
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task LiveConnection_ShouldBeDropped_WhenTheUsersAccessIsRevoked()
    {
        _status.Verdict = UserAccessVerdict.Allowed;
        await using var connection = Connect(new AccessToken("acme", "alice", Now + TokenLifetime));
        var closed = ClosedSignal(connection);
        await connection.StartAsync();
        (await connection.InvokeAsync<string>(nameof(ProbeHub.Ping))).Should().Be("pong");

        await Bus.PublishAsync(new UserAccessRevokedEvent("acme", "alice", "suspended"));

        await closed.WaitAsync(GuardTimeout);
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task LiveConnection_ShouldNotReconnect_WhenTheUsersAccessIsRevoked()
    {
        // The contrast to the expiry close below, and the proof this harness tells the two apart:
        // a revocation aborts, so SignalR tells even a reconnecting client to stay away.
        _status.Verdict = UserAccessVerdict.Allowed;
        await using var connection = Connect(new AccessToken("acme", "alice", Now + TokenLifetime), reconnect: true);
        var closed = ClosedSignal(connection);
        var reconnecting = ReconnectingSignal(connection);
        await connection.StartAsync();
        (await connection.InvokeAsync<string>(nameof(ProbeHub.Ping))).Should().Be("pong");

        await Bus.PublishAsync(new UserAccessRevokedEvent("acme", "alice", "suspended"));

        await closed.WaitAsync(GuardTimeout);
        reconnecting.IsCompleted.Should().BeFalse(because: "a revoked account's client must not come back on its own");
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task LiveConnection_ShouldBeClosedAtTokenExpiry_WhenAdmitted(string verdict)
    {
        // Allowed: the Api vouched for the account at connect, but a revocation can still miss this
        // connection (a lost event, a stale cached status, sessions revoked for an account that is
        // still active). Unavailable: for an account suspended before this connect the revocation
        // has already fired. Either way, only the token's own expiry still ends the connection.
        _status.Verdict = Enum.Parse<UserAccessVerdict>(verdict);
        await using var connection = Connect(new AccessToken("acme", "alice", Now + TokenLifetime));
        var closed = ClosedSignal(connection);
        await connection.StartAsync();
        (await connection.InvokeAsync<string>(nameof(ProbeHub.Ping))).Should().Be("pong");

        _time.Advance(TokenLifetime - TimeSpan.FromSeconds(1));
        (await connection.InvokeAsync<string>(nameof(ProbeHub.Ping))).Should().Be("pong",
            because: "the token is valid for one more second");

        _time.Advance(TimeSpan.FromSeconds(1));

        await closed.WaitAsync(GuardTimeout);
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task LiveConnection_ShouldReconnectWithTheClientsCurrentToken_WhenClosedAtTokenExpiry(string verdict)
    {
        // What the Web client does: withAutomaticReconnect, and an accessTokenFactory that returns
        // the token the app holds now, refreshed before the old one expired.
        _status.Verdict = Enum.Parse<UserAccessVerdict>(verdict);
        var token = new AccessToken("acme", "alice", Now + TokenLifetime);
        await using var connection = Connect(token, reconnect: true);
        var closed = ClosedSignal(connection);
        var reconnected = ReconnectedSignal(connection);
        await connection.StartAsync();
        (await connection.InvokeAsync<long>(nameof(ProbeHub.TokenExpiry)))
            .Should().Be((Now + TokenLifetime).ToUnixTimeSeconds());

        var refreshedExpiry = Now + TokenLifetime + TokenLifetime;
        token.ExpiresAt = refreshedExpiry;
        _time.Advance(TokenLifetime);

        await reconnected.WaitAsync(GuardTimeout);
        (await connection.InvokeAsync<long>(nameof(ProbeHub.TokenExpiry))).Should().Be(refreshedExpiry.ToUnixTimeSeconds(),
            because: "the reconnect must present the token the client holds now");
        closed.IsCompleted.Should().BeFalse(because: "a token expiry is not a refusal: the client stays with the hub");
        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task LiveConnection_ShouldStayOpen_WhenAnotherUsersAccessIsRevoked()
    {
        _status.Verdict = UserAccessVerdict.Allowed;
        await using var alice = Connect(new AccessToken("acme", "alice", Now + TokenLifetime));
        await using var bob = Connect(new AccessToken("acme", "bob", Now + TokenLifetime));
        var bobClosed = ClosedSignal(bob);
        await alice.StartAsync();
        await bob.StartAsync();
        // The handshake completes before OnConnectedAsync registers a connection; a hub-method reply
        // is the signal that it has run, so the revocation below cannot overtake bob's registration.
        (await bob.InvokeAsync<string>(nameof(ProbeHub.Ping))).Should().Be("pong");

        await Bus.PublishAsync(new UserAccessRevokedEvent("acme", "bob", "deactivated"));

        // Bob's close proves the revocation was processed; Alice must be untouched by it.
        await bobClosed.WaitAsync(GuardTimeout);
        (await alice.InvokeAsync<string>(nameof(ProbeHub.Ping))).Should().Be("pong");
        alice.State.Should().Be(HubConnectionState.Connected);
    }

    private IPushEventBus Bus => _app.Services.GetRequiredService<IPushEventBus>();

    private HubConnection Connect(AccessToken token, bool reconnect = false)
    {
        var builder = new HubConnectionBuilder().WithUrl(_hubUrl, options =>
            options.AccessTokenProvider = () => Task.FromResult<string?>(token.Encode()));
        if (reconnect)
            builder.WithAutomaticReconnect();
        return builder.Build();
    }

    private static Task<Exception?> ClosedSignal(HubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    private static Task ReconnectingSignal(HubConnection connection)
    {
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnecting += _ =>
        {
            reconnecting.TrySetResult();
            return Task.CompletedTask;
        };
        return reconnecting.Task;
    }

    private static Task ReconnectedSignal(HubConnection connection)
    {
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += _ =>
        {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        };
        return reconnected.Task;
    }

    [Authorize]
    public sealed class ProbeHub : Hub
    {
        // Instance methods on purpose: SignalR only exposes instance methods as hub methods.
#pragma warning disable CA1822
        public string Ping() => "pong";
#pragma warning restore CA1822

        /// <summary>The <c>exp</c> of the token this connection was opened with.</summary>
        public long TokenExpiry() =>
            long.Parse(Context.User!.FindFirst("exp")!.Value, CultureInfo.InvariantCulture);
    }

    /// <summary>A client's access token; the client presents whatever it holds when it (re)connects.</summary>
    private sealed class AccessToken(string tenantId, string userId, DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;

        public string Encode() =>
            string.Join('|', tenantId, userId, ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    private sealed class SwitchableStatusClient : IUserAccessStatusClient
    {
        public UserAccessVerdict Verdict { get; set; } = UserAccessVerdict.Allowed;

        public Task<UserAccessVerdict> CheckAsync(string tenantId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult(Verdict);
    }

    /// <summary>
    /// Authenticates a bearer token <c>tid|sub|exp</c> — the claims a Platform.Api JWT carries
    /// (<c>exp</c> in Unix seconds, as in the JWT) — from the Authorization header the .NET client
    /// sends, or from <c>access_token</c>, where a browser client puts it.
    /// </summary>
    private sealed class TokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Token";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal)
                ? header["Bearer ".Length..]
                : Request.Query["access_token"].ToString();
            if (token.Split('|') is not [var tid, var sub, var exp])
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
            [
                new Claim("tid", tid),
                new Claim("sub", sub),
                new Claim("exp", exp, ClaimValueTypes.Integer64),
            ], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
