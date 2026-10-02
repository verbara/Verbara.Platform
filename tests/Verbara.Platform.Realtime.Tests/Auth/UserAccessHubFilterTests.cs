using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Realtime.Auth;
using Verbara.Platform.Realtime.Clients;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Verbara.Platform.Realtime.Tests.Auth;

/// <summary>
/// The hub filter is the connect-time half of revocation: one status lookup per connection
/// (Realtime only sees the JWT, which outlives a suspension by up to its 15-minute lifetime), and
/// every admitted connection registered so a revocation event can abort it. Every admitted
/// connection is also closed when the token that opened it expires — the bound for whatever
/// revocation the connection did not hear about — in a way that lets the client reconnect.
/// </summary>
public sealed class UserAccessHubFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);


    [Fact]
    public async Task OnConnectedAsync_ShouldRefuseTheConnection_WhenAccountMayNotConnect()
    {
        var (filter, registry, status, _) = NewFilter(UserAccessVerdict.Denied);
        var caller = new FakeCallerContext(AccessToken("acme", "alice"));
        var nextCalled = false;

        var act = () => filter.OnConnectedAsync(Lifetime(caller), _ => { nextCalled = true; return Task.CompletedTask; });

        await act.Should().ThrowAsync<HubException>(
            because: "throwing from OnConnected closes the connection with allowReconnect=false");
        nextCalled.Should().BeFalse(because: "the hub must never join groups or track presence for the user");
        registry.AbortAll("acme", "alice").Should().Be(0, because: "a refused connection is not left registered");
        status.Calls.Should().ContainSingle().Which.Should().Be(("acme", "alice"));
    }

    [Fact]
    public async Task OnConnectedAsync_ShouldAdmitAndTrackTheConnection_WhenAccountIsActive()
    {
        var (filter, registry, _, _) = NewFilter(UserAccessVerdict.Allowed);
        var caller = new FakeCallerContext(AccessToken("acme", "alice"));
        var nextCalled = false;

        await filter.OnConnectedAsync(Lifetime(caller), _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        registry.AbortAll("acme", "alice").Should().Be(1);
        caller.AbortCount.Should().Be(1, because: "a revocation for the user aborts this live connection");
    }

    [Fact]
    public async Task OnConnectedAsync_ShouldAdmitAndTrackTheConnection_WhenStatusLookupIsUnavailable()
    {
        // Fail-open on an infrastructure failure, bounded by the token's own expiry (below), and
        // the connection is still registered, so a revocation event still aborts it before then.
        var (filter, registry, _, _) = NewFilter(UserAccessVerdict.Unavailable);
        var caller = new FakeCallerContext(AccessToken("acme", "alice"));
        var nextCalled = false;

        await filter.OnConnectedAsync(Lifetime(caller), _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        caller.CloseRequests.Should().Be(0, because: "the token is still valid");
        caller.AbortCount.Should().Be(0, because: "the token is still valid");
        registry.AbortAll("acme", "alice").Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task OnConnectedAsync_ShouldCloseTheConnectionAtTokenExpiry_WhenAdmitted(string verdict)
    {
        // Allowed included: a connection the Api vouched for at connect can still miss the
        // revocation that should end it (a lost event, a stale cached status, sessions revoked for a
        // still-active account). Only the token's own expiry bounds it.
        var (filter, _, _, time) = NewFilter(Enum.Parse<UserAccessVerdict>(verdict));
        var caller = new FakeCallerContext(AccessToken("acme", "alice", expiresAt: Now + TokenLifetime));
        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        time.Advance(TokenLifetime - TimeSpan.FromSeconds(1));
        caller.CloseRequests.Should().Be(0, because: "the token is valid for one more second");

        time.Advance(TimeSpan.FromSeconds(1));
        caller.CloseRequests.Should().Be(1, because: "no live connection may outlive the token that opened it");
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task OnConnectedAsync_ShouldLetTheClientReconnect_WhenClosingAtTokenExpiry(string verdict)
    {
        // Abort() makes SignalR send allowReconnect=false, the refusal reserved for an account that
        // may no longer connect. An expired token is not that: the client must come back with the
        // token it holds now, which authentication and the status lookup judge afresh.
        var (filter, _, _, time) = NewFilter(Enum.Parse<UserAccessVerdict>(verdict));
        var caller = new FakeCallerContext(AccessToken("acme", "alice", expiresAt: Now + TokenLifetime));
        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        time.Advance(TokenLifetime);

        caller.CloseRequests.Should().Be(1, because: "a close request is the close SignalR lets the client reconnect from");
        caller.AbortCount.Should().Be(0, because: "an abort would tell the client never to reconnect");
    }

    [Fact]
    public async Task OnConnectedAsync_ShouldAbortTheConnectionAtTokenExpiry_WhenItsTransportCannotBeAskedToClose()
    {
        // Closing is the hard requirement; letting the client reconnect is the preference.
        var (filter, _, _, time) = NewFilter(UserAccessVerdict.Allowed);
        var caller = new FakeCallerContext(
            AccessToken("acme", "alice", expiresAt: Now + TokenLifetime), canRequestClose: false);
        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        time.Advance(TokenLifetime);

        caller.AbortCount.Should().Be(1, because: "without a close request to make, the connection must still end");
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task OnConnectedAsync_ShouldCloseTheConnectionAtOnce_WhenTheTokenCarriesNoExpiry(string verdict)
    {
        var (filter, _, _, _) = NewFilter(Enum.Parse<UserAccessVerdict>(verdict));
        var caller = new FakeCallerContext(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "alice"), new Claim("tid", "acme")], "test")));

        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        caller.CloseRequests.Should().Be(1, because: "a connection that no token expiry bounds must not stand");
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task OnConnectedAsync_ShouldNotCloseTheConnectionWhileTheHubAdmitsIt_WhenTheTokenExpiresMeanwhile(string verdict)
    {
        // A close that lands inside the hub's own OnConnectedAsync can make it throw, and SignalR
        // answers a throwing OnConnectedAsync with allowReconnect=false. The close waits for it.
        var (filter, _, _, time) = NewFilter(Enum.Parse<UserAccessVerdict>(verdict));
        var caller = new FakeCallerContext(AccessToken("acme", "alice", expiresAt: Now + TimeSpan.FromMinutes(1)));
        var closesWhileAdmitting = -1;

        await filter.OnConnectedAsync(Lifetime(caller), _ =>
        {
            time.Advance(TimeSpan.FromMinutes(2));
            closesWhileAdmitting = caller.CloseRequests + caller.AbortCount;
            return Task.CompletedTask;
        });

        closesWhileAdmitting.Should().Be(0, because: "the hub was still admitting the connection");
        caller.CloseRequests.Should().Be(1, because: "the token expired meanwhile, so the close follows the admission at once");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnConnectedAsync_ShouldContainTheExpiryCloseFailure_WhenClosingTheConnectionThrows(bool alreadyDisposed)
    {
        var (filter, _, _, time) = NewFilter(UserAccessVerdict.Allowed);
        Exception failure = alreadyDisposed
            ? new ObjectDisposedException("connection")
            : new InvalidOperationException("transport failed");
        var caller = new FakeCallerContext(
            AccessToken("acme", "alice", expiresAt: Now + TokenLifetime), closeFailure: failure);
        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        var act = () => time.Advance(TokenLifetime);

        act.Should().NotThrow(because: "the close runs on a timer thread, where an escaping exception ends the process");
        caller.CloseRequests.Should().Be(1);
    }

    [Fact]
    public async Task OnConnectedAsync_ShouldCheckTheImpersonatorsHomeTenant_WhenTokenIsAnImpersonation()
    {
        var (filter, registry, status, _) = NewFilter(UserAccessVerdict.Allowed);
        var caller = new FakeCallerContext(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "admin-1"),
            new Claim("tid", "customer-x"),
            new Claim("impersonator_id", "admin-1"),
            new Claim("impersonator_tenant", "platform"),
            ExpiryClaim(Now + TimeSpan.FromMinutes(30)),
        ], "test")));

        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        status.Calls.Should().ContainSingle().Which.Should().Be(("platform", "admin-1"));
        registry.AbortAll("platform", "admin-1").Should().Be(1,
            because: "suspending the admin must also cut the realtime connection of their impersonation session");
    }

    [Fact]
    public async Task OnConnectedAsync_ShouldRefuseTheConnection_WhenPrincipalCarriesNoUser()
    {
        var (filter, _, status, _) = NewFilter(UserAccessVerdict.Allowed);
        var caller = new FakeCallerContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "acme")], "test")));

        var act = () => filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        await act.Should().ThrowAsync<HubException>(
            because: "every token Platform.Api mints carries sub + tid; one without them cannot be status-checked");
        status.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task OnConnectedAsync_ShouldStopTrackingTheConnection_WhenTheHubRejectsIt(string verdict)
    {
        var (filter, registry, _, time) = NewFilter(Enum.Parse<UserAccessVerdict>(verdict));
        var caller = new FakeCallerContext(AccessToken("acme", "alice", expiresAt: Now + TokenLifetime));

        var act = () => filter.OnConnectedAsync(Lifetime(caller), _ => throw new InvalidOperationException("hub refused"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        registry.AbortAll("acme", "alice").Should().Be(0,
            because: "SignalR skips OnDisconnected when OnConnected throws, so the filter must clean up itself");
        time.Advance(TokenLifetime);
        (caller.CloseRequests + caller.AbortCount).Should().Be(0,
            because: "a connection that never opened has no expiry close pending");
    }

    [Fact]
    public async Task OnDisconnectedAsync_ShouldStopTrackingTheConnection_WhenTheClientDisconnects()
    {
        var (filter, registry, _, _) = NewFilter(UserAccessVerdict.Allowed);
        var caller = new FakeCallerContext(AccessToken("acme", "alice"));
        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);
        var nextCalled = false;

        await filter.OnDisconnectedAsync(Lifetime(caller), exception: null, (_, _) => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        registry.AbortAll("acme", "alice").Should().Be(0);
        caller.AbortCount.Should().Be(0);
    }

    [Theory]
    [InlineData(nameof(UserAccessVerdict.Allowed))]
    [InlineData(nameof(UserAccessVerdict.Unavailable))]
    public async Task OnDisconnectedAsync_ShouldCancelTheExpiryClose_WhenTheConnectionEndsBeforeItsToken(string verdict)
    {
        var (filter, _, _, time) = NewFilter(Enum.Parse<UserAccessVerdict>(verdict));
        var caller = new FakeCallerContext(AccessToken("acme", "alice", expiresAt: Now + TokenLifetime));
        await filter.OnConnectedAsync(Lifetime(caller), _ => Task.CompletedTask);

        await filter.OnDisconnectedAsync(Lifetime(caller), exception: null, (_, _) => Task.CompletedTask);
        time.Advance(TokenLifetime);

        (caller.CloseRequests + caller.AbortCount).Should().Be(0,
            because: "a connection that is already gone has nothing left to close");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static (UserAccessHubFilter Filter, LiveConnectionRegistry Registry, FakeStatusClient Status, FakeTimeProvider Time) NewFilter(
        UserAccessVerdict verdict)
    {
        var registry = new LiveConnectionRegistry();
        var status = new FakeStatusClient(verdict);
        var time = new FakeTimeProvider(Now);
        var filter = new UserAccessHubFilter(registry, status, time, NullLogger<UserAccessHubFilter>.Instance);
        return (filter, registry, status, time);
    }

    /// <summary>The claims Realtime's JwtBearer surfaces for a Platform.Api access token.</summary>
    private static ClaimsPrincipal AccessToken(string tenantId, string userId, DateTimeOffset? expiresAt = null) =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", userId),
            new Claim("tid", tenantId),
            ExpiryClaim(expiresAt ?? Now + TokenLifetime),
        ], "test"));

    private static Claim ExpiryClaim(DateTimeOffset expiresAt) =>
        new("exp", expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);

    private static HubLifetimeContext Lifetime(HubCallerContext caller) =>
        new(caller, new ServiceCollection().BuildServiceProvider(), new TestHub());

    private sealed class TestHub : Hub;

    private sealed class FakeStatusClient(UserAccessVerdict verdict) : IUserAccessStatusClient
    {
        public List<(string TenantId, string UserId)> Calls { get; } = [];

        public Task<UserAccessVerdict> CheckAsync(string tenantId, string userId, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, userId));
            return Task.FromResult(verdict);
        }
    }

    /// <summary>
    /// A hub connection whose two ways of ending are counted apart: <see cref="Abort"/>, after which
    /// SignalR tells the client not to reconnect, and a close request through the transport's
    /// <see cref="IConnectionLifetimeNotificationFeature"/>, after which it tells the client it may.
    /// </summary>
    private sealed class FakeCallerContext : HubCallerContext
    {
        private readonly Exception? _closeFailure;

        public FakeCallerContext(ClaimsPrincipal user, Exception? closeFailure = null, bool canRequestClose = true)
        {
            User = user;
            _closeFailure = closeFailure;
            if (canRequestClose)
                Features.Set<IConnectionLifetimeNotificationFeature>(new CloseRequestFeature(this));
        }

        public int AbortCount { get; private set; }

        public int CloseRequests { get; private set; }

        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User { get; }

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
            AbortCount++;
            if (_closeFailure is not null)
                throw _closeFailure;
        }

        private void RequestClose()
        {
            CloseRequests++;
            if (_closeFailure is not null)
                throw _closeFailure;
        }

        private sealed class CloseRequestFeature(FakeCallerContext connection) : IConnectionLifetimeNotificationFeature
        {
            public CancellationToken ConnectionClosedRequested { get; set; }

            public void RequestClose() => connection.RequestClose();
        }
    }
}
