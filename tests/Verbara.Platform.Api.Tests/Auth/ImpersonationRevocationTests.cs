using System.Net;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// An impersonation token is an Admin bearer token for another tenant that lives 30 minutes. Its
/// session can be closed three ways — the impersonator ends it, an admin revokes it, the timeout
/// sweep expires it — and each must stop the token authenticating at once, not when it expires.
/// Every case runs through the real pipeline, with a Customer target (the common case), and checks
/// the token worked before the session closed, so a refusal can only come from the close.
/// </summary>
public sealed class ImpersonationRevocationTests : IClassFixture<ImpersonationApiFactory>, IAsyncLifetime
{
    private readonly ImpersonationApiFactory _factory;
    private readonly List<string> _startedSessions = [];

    public ImpersonationRevocationTests(ImpersonationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenSessionEnded()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin());
        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.OK,
            because: "the token works in the target tenant while its session is open");

        (await _factory.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.NoContent);

        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "an ended impersonation must not keep Admin access to another tenant for the rest of its token's life");
    }

    [Fact]
    public async Task EndImpersonation_ShouldSucceed_WhenTargetIsCustomerTenant()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin());

        (await _factory.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.NoContent,
            because: "an impersonation token's tenant is its target, so ending must not require a platform or Partner tenant");

        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.Completed);
    }

    [Fact]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenAdminRevokesSession()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin());
        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.OK);

        (await _factory.RevokeSessionAsync(session.SessionId)).Should().Be(HttpStatusCode.NoContent);

        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.ManuallyRevoked);
        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "a revoked session's token must stop working, not live on until it expires");
    }

    [Fact]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenSessionTimesOut()
    {
        // A tenant whose impersonation timeout (1 minute) is shorter than the token's 30 minutes.
        var actor = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 1);
        var session = await StartAsync(actor.Admin, actor.CustomerTenantId);
        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.OK);

        await _factory.SweepAsync(elapsed: TimeSpan.FromMinutes(2));

        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.AutoTimedOut);
        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "a session the tenant's timeout closed must not leave its token working");
    }

    [Fact]
    public async Task EndImpersonation_ShouldReturn401_WhenTokenAlreadyEnded()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin());
        (await _factory.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.NoContent);

        (await _factory.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "an ended token authenticates nothing, its own end included");
    }

    [Fact]
    public async Task EndImpersonation_ShouldRefuse_WhenCallerIsNotImpersonating()
    {
        // Ending is open to any authenticated caller: the impersonation token's tenant is its
        // target, never the platform. The handler itself refuses a token that impersonates nobody.
        var customerAdmin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)!;

        (await _factory.EndImpersonationAsync(_factory.MintAccessToken(customerAdmin))).Should().Be(HttpStatusCode.BadRequest);
        (await _factory.EndImpersonationAsync(_factory.PlatformAdminToken())).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EndImpersonation_ShouldReturn401_WhenUnauthenticated()
    {
        using var client = _factory.CreateClient();

        using var response = await client.DeleteAsync("/api/v1/management/impersonate");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokingOneSession_ShouldNotAffectOtherImpersonationTokens()
    {
        var admin = _factory.NewPlatformAdmin();
        var revoked = await StartAsync(admin);
        var other = await StartAsync(admin);

        (await _factory.RevokeSessionAsync(revoked.SessionId)).Should().Be(HttpStatusCode.NoContent);

        (await _factory.ListUsersAsync(revoked.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.ListUsersAsync(other.AccessToken)).Should().Be(HttpStatusCode.OK,
            because: "revoking a session revokes its own token, not every token of its impersonator");
    }

    [Fact]
    public async Task ImpersonatorOwnAccessToken_ShouldKeepWorking_WhenImpersonationEnds()
    {
        var admin = _factory.NewPlatformAdmin();
        var session = await StartAsync(admin);

        (await _factory.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.NoContent);

        (await _factory.ListUsersAsync(_factory.MintAccessToken(admin))).Should().Be(HttpStatusCode.OK,
            because: "ending an impersonation revokes the impersonation token, not the impersonator's own sign-in");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // The session store caps concurrent sessions per actor tenant: close whatever a test left open.
    public Task DisposeAsync() => _factory.CloseSessionsAsync(_startedSessions);

    private async Task<StartedImpersonation> StartAsync(User admin, string targetTenantId = AccountStatusApiFactory.CustomerTenantId)
    {
        var session = await _factory.StartImpersonationAsync(admin, targetTenantId);
        _startedSessions.Add(session.SessionId);
        return session;
    }
}
