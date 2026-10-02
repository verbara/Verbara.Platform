using System.Net;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Realtime.Clients;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Realtime.Tests.Hosting;

/// <summary>
/// Account-status enforcement as the shipped Realtime host composes it. The unit and in-process
/// suites prove <c>AddUserAccessEnforcement</c> works on a host built for the test; these prove
/// Program.cs actually wires it: the hub filter runs on <c>/hubs/platform</c>, the revocation
/// listener runs as a hosted service, and the real JwtBearer pipeline hands the filter the token
/// expiry every connection is closed at — and refuses a token that has already reached it.
/// </summary>
public sealed class RealtimeHostCompositionTests : IClassFixture<RealtimeHostFixture>
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(15);

    private readonly RealtimeHostFixture _host;

    public RealtimeHostCompositionTests(RealtimeHostFixture host) => _host = host;


    [Fact]
    public async Task Connect_ShouldBeRefusedByTheHost_WhenAccountIsNotActive()
    {
        _host.Status.Verdict = UserAccessVerdict.Denied;
        await using var connection = _host.Connect("acme", NewUserId(), DateTimeOffset.UtcNow.AddMinutes(15));
        var closed = ClosedSignal(connection);

        _ = await Record.ExceptionAsync(() => connection.StartAsync());

        var error = await closed.WaitAsync(GuardTimeout);
        error.Should().NotBeNull(because: "the host's hub filter refuses the connection");
        error!.Message.Should().Contain("Account is not active");
    }

    [Fact]
    public async Task LiveConnection_ShouldBeDroppedByTheHost_WhenTheUsersAccessIsRevoked()
    {
        _host.Status.Verdict = UserAccessVerdict.Allowed;
        var userId = NewUserId();
        await using var connection = _host.Connect("acme", userId, DateTimeOffset.UtcNow.AddMinutes(15));
        var closed = ClosedSignal(connection);
        using var presence = _host.WatchPresence(userId);
        await connection.StartAsync();
        // SignalR completes the handshake before OnConnectedAsync, where the filter registers the
        // connection: wait for the hub to announce the agent's presence, which it does from inside
        // the filter's admission. (A revocation landing earlier is the status lookup's job, and the
        // stub here keeps answering Allowed.)
        await presence.Announced.WaitAsync(GuardTimeout);

        await _host.PublishAsync(new UserAccessRevokedEvent("acme", userId, "suspended"));

        await closed.WaitAsync(GuardTimeout);
        connection.State.Should().Be(HubConnectionState.Disconnected);
        _host.Services.GetServices<IHostedService>().OfType<UserAccessRevocationListener>()
            .Should().ContainSingle(because: "the pod drains its own connections; one listener, never zero or two");
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task LiveConnection_ShouldBeClosedByTheHostAtJwtExpiry_WhenAdmitted(string verdict)
    {
        // Over WebSockets the token is checked once, at the upgrade: only the filter's expiry
        // close, fed the exp claim by the host's real JwtBearer pipeline, can end this connection.
        _host.Status.Verdict = Enum.Parse<UserAccessVerdict>(verdict);
        var token = _host.MintAccessToken("acme", NewUserId(), DateTimeOffset.UtcNow.AddSeconds(3));
        await using var connection = _host.ConnectOverWebSockets(() => token);
        var closed = ClosedSignal(connection);
        await connection.StartAsync();

        await closed.WaitAsync(GuardTimeout);
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task LiveConnection_ShouldReconnectThroughTheHostWithAFreshJwt_WhenClosedAtJwtExpiry()
    {
        // The Web client's shape: automatic reconnect, and a token factory that returns the token the
        // app holds by then (it refreshes a minute before expiry; here, right after connecting).
        _host.Status.Verdict = UserAccessVerdict.Allowed;
        var userId = NewUserId();
        var token = _host.MintAccessToken("acme", userId, DateTimeOffset.UtcNow.AddSeconds(3));
        await using var connection = _host.ConnectOverWebSockets(() => token, reconnect: true);
        var closed = ClosedSignal(connection);
        var reconnected = ReconnectedSignal(connection);
        await connection.StartAsync();

        token = _host.MintAccessToken("acme", userId, DateTimeOffset.UtcNow.AddMinutes(15));

        await reconnected.WaitAsync(GuardTimeout);
        closed.IsCompleted.Should().BeFalse(because: "the host closed the connection as one to come back to, not as a refusal");
        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Connect_ShouldBeRefusedByTheHost_WhenTheJwtExpiredWithinTheClockSkew()
    {
        // JwtBearer would accept this token for another 25 seconds (30 s clock skew). Admitted, it
        // would be closed at once — and a reconnecting client would loop through admit-and-close
        // until the skew ran out. Refused, its reconnect fails like any 401 and backs off.
        _host.Status.Verdict = UserAccessVerdict.Allowed;
        var token = _host.MintAccessToken("acme", NewUserId(), DateTimeOffset.UtcNow.AddSeconds(-5));
        await using var connection = _host.ConnectOverWebSockets(() => token);

        var act = () => connection.StartAsync();

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string NewUserId() => $"host-user-{Guid.NewGuid():N}";

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
}
