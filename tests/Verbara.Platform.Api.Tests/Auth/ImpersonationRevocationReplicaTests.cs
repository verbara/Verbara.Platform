using System.Net;
using Verbara.Platform.Core.Impersonation;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Two replicas of the Api, as the default Helm chart runs: they share the jti revocation store
/// (Redis) and the signing key, while each keeps its own in-memory impersonation session store.
/// A session closed on one replica must stop its token on the other — which has no record of the
/// session at all, so only the shared revocation can stop it there.
/// </summary>
public sealed class ImpersonationRevocationReplicaTests
    : IClassFixture<ImpersonationRevocationReplicaTests.TwoReplicas>, IAsyncLifetime
{
    private readonly ImpersonationApiFactory _replicaA;
    private readonly WebApplicationFactory<Program> _replicaB;
    private readonly List<string> _startedSessions = [];

    public ImpersonationRevocationReplicaTests(TwoReplicas replicas)
    {
        _replicaA = replicas.A;
        _replicaB = replicas.B;
    }

    [Theory]
    [InlineData("end")]
    [InlineData("revoke")]
    [InlineData("sweep")]
    public async Task ImpersonationToken_ShouldStopAuthenticatingOnEveryReplica_WhenSessionClosesOnOne(string close)
    {
        var (admin, target) = close == "sweep"
            ? await PartnerActorAsync()
            : (_replicaA.NewPlatformAdmin(_replicaB), AccountStatusApiFactory.CustomerTenantId);
        var session = await _replicaA.StartImpersonationAsync(admin, target);
        _startedSessions.Add(session.SessionId);
        (await _replicaA.ListUsersAsync(session.AccessToken, _replicaB)).Should().Be(HttpStatusCode.OK,
            because: "replica B accepts replica A's token before anything closes, so a later refusal can only come from the close");

        switch (close)
        {
            case "end":
                (await _replicaA.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.NoContent);
                break;
            case "revoke":
                (await _replicaA.RevokeSessionAsync(session.SessionId)).Should().Be(HttpStatusCode.NoContent);
                break;
            default:
                await _replicaA.SweepAsync(elapsed: TimeSpan.FromMinutes(2));
                (await _replicaA.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.AutoTimedOut);
                break;
        }

        (await _replicaA.ListUsersAsync(session.AccessToken, _replicaB)).Should().Be(HttpStatusCode.Unauthorized,
            because: $"a session closed by '{close}' on replica A must stop its token on replica B too");
    }

    [Fact]
    public async Task EndImpersonation_ShouldStopTheTokenEverywhere_WhenSentToAReplicaThatDoesNotHoldTheSession()
    {
        // Ending revokes by the token's own claims, so it needs no session record on the replica
        // that serves it — a load balancer may send the end anywhere.
        var session = await _replicaA.StartImpersonationAsync(_replicaA.NewPlatformAdmin(_replicaB));
        _startedSessions.Add(session.SessionId);
        (await _replicaA.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.OK);

        (await _replicaA.EndImpersonationAsync(session.AccessToken, _replicaB)).Should().Be(HttpStatusCode.NoContent);

        (await _replicaA.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "the replica holding the session must refuse a token ended on another replica");
    }

    [Fact]
    public async Task RevokeSession_ShouldReturn404_WhenSentToAReplicaThatDoesNotHoldTheSession()
    {
        // Characterization of the per-replica session store: the admin list and revoke only see the
        // sessions of the replica that serves the request. A shared session store would flip this.
        var session = await _replicaA.StartImpersonationAsync(_replicaA.NewPlatformAdmin(_replicaB));
        _startedSessions.Add(session.SessionId);

        (await _replicaA.RevokeSessionAsync(session.SessionId, _replicaB)).Should().Be(HttpStatusCode.NotFound);

        (await _replicaA.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.Active);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _replicaA.CloseSessionsAsync(_startedSessions);

    private async Task<(Verbara.Platform.Identity.User Admin, string Target)> PartnerActorAsync()
    {
        var actor = await _replicaA.NewPartnerActorAsync(impersonationTimeoutMinutes: 1, _replicaB);
        return (actor.Admin, actor.CustomerTenantId);
    }

    /// <summary>Replica A and a replica B built from it (<see cref="ImpersonationApiFactory.CreateReplica"/>).</summary>
    public sealed class TwoReplicas : IDisposable
    {
        public TwoReplicas()
        {
            A = new ImpersonationApiFactory();
            B = A.CreateReplica();
        }

        public ImpersonationApiFactory A { get; }

        public WebApplicationFactory<Program> B { get; }

        public void Dispose()
        {
            B.Dispose();
            A.Dispose();
        }
    }
}
