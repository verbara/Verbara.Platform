using System.Net;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The jti revocation store (Redis in production) is unreachable. An impersonation token can then
/// not be shown to be unrevoked, so it is refused (a server error, not a 401: the outage is the
/// server's, and a 401 would send the console into its token-refresh path); a revoke cannot be
/// recorded, so the session stays open for the admin to retry rather than closing with its token
/// still valid. Ordinary access tokens never consult the store and keep working.
/// </summary>
public sealed class ImpersonationRevocationOutageTests
    : IClassFixture<ImpersonationRevocationOutageTests.UnreachableRevocationStoreApiFactory>, IAsyncLifetime
{
    private readonly UnreachableRevocationStoreApiFactory _factory;
    private readonly List<string> _startedSessions = [];

    public ImpersonationRevocationOutageTests(UnreachableRevocationStoreApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ImpersonationRequest_ShouldFailClosed_WhenRevocationStoreUnavailable()
    {
        var admin = _factory.NewPlatformAdmin();
        var session = await StartAsync(admin);

        var status = await _factory.ListUsersAsync(session.AccessToken);

        ((int)status).Should().BeGreaterThanOrEqualTo(500,
            because: "an impersonation token whose revocation cannot be checked must not be admitted");
        (await _factory.ListUsersAsync(_factory.MintAccessToken(admin))).Should().Be(HttpStatusCode.OK,
            because: "an ordinary access token does not depend on the revocation store");
    }

    [Fact]
    public async Task RevokeSession_ShouldKeepSessionActive_WhenTokenRevocationFails()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin());

        var status = await _factory.RevokeSessionAsync(session.SessionId);

        ((int)status).Should().BeGreaterThanOrEqualTo(500,
            because: "a revoke that could not revoke the token must not report success");
        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.Active);
        (await _factory.ListActiveSessionIdsAsync()).Should().Contain(session.SessionId,
            because: "the session stays listed so the admin can retry the revoke");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.CloseSessionsAsync(_startedSessions);

    private async Task<StartedImpersonation> StartAsync(Verbara.Platform.Identity.User admin)
    {
        var session = await _factory.StartImpersonationAsync(admin);
        _startedSessions.Add(session.SessionId);
        return session;
    }

    /// <summary>An <see cref="ImpersonationApiFactory"/> whose jti revocation store cannot be reached.</summary>
    public sealed class UnreachableRevocationStoreApiFactory : ImpersonationApiFactory
    {
        /// <inheritdoc />
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);
            services.RemoveAll<IJtiRevocationCache>();
            services.AddSingleton<IJtiRevocationCache>(new UnreachableJtiRevocationCache());
        }
    }

    /// <summary>Fails every call the way the Redis-backed store does when Redis is down.</summary>
    private sealed class UnreachableJtiRevocationCache : IJtiRevocationCache
    {
        public ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct) => throw Unreachable();

        public ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct) => throw Unreachable();

        private static RedisConnectionException Unreachable() =>
            new(ConnectionFailureType.UnableToConnect, CommandFlags.None,
                "No connection is available to service this operation.", innerException: null, CommandStatus.Unknown);
    }
}
