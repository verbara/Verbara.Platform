using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Impersonation;

/// <summary>
/// R5.2 PB.2 / C.7 — unit tests for the periodic auto-timeout sweep.
/// The service is exercised through its public <c>SweepOnceAsync</c> hook so
/// the loop's <see cref="PeriodicTimer"/> never has to fire (deterministic +
/// fast).
/// </summary>
public sealed class SessionTimeoutServiceTests
{
    private const string ActorTenant = "tenant-actor";
    private const string TargetTenant = "tenant-target";
    private const string ActorUser = "user-admin-1";

    [Fact]
    public async Task SweepOnceAsync_ShouldRevokeExpiredSessions_WhenElapsedExceedsTimeout()
    {
        var (service, store, _, _, clock, _) = CreateService(autoTimeoutMinutes: 240);

        var session = await StartSessionAsync(store, clock);

        // Advance past the timeout (4h + 1min).
        clock.AdvanceMinutes(241);

        await service.SweepOnceAsync(CancellationToken.None);

        var refreshed = await store.GetAsync(session.Id, CancellationToken.None);
        refreshed.Should().NotBeNull();
        refreshed!.Status.Should().Be(ImpersonationSessionStatus.AutoTimedOut);
        refreshed.CloseReason.Should().Be("auto_timeout");
        refreshed.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldNotRevoke_WhenStillWithinTimeout()
    {
        var (service, store, _, _, clock, revocations) = CreateService(autoTimeoutMinutes: 20);

        var session = await StartSessionAsync(store, clock);

        // Halfway through the budget, with the session's token still live — both must survive.
        clock.AdvanceMinutes(10);

        await service.SweepOnceAsync(CancellationToken.None);

        var refreshed = await store.GetAsync(session.Id, CancellationToken.None);
        refreshed.Should().NotBeNull();
        refreshed!.Status.Should().Be(ImpersonationSessionStatus.Active);
        refreshed.EndedAt.Should().BeNull();
        (await revocations.IsRevokedAsync(session.TokenId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldRevokeTokenId_WhenSessionTimesOut()
    {
        // A tenant timeout (10 min) shorter than the token's 30-minute life: closing the session
        // must stop its token, or the token outlives the session by 20 minutes.
        var (service, store, _, _, clock, revocations) = CreateService(autoTimeoutMinutes: 10);
        var session = await StartSessionAsync(store, clock);
        clock.AdvanceMinutes(11);

        await service.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(session.Id, CancellationToken.None))!.Status.Should().Be(ImpersonationSessionStatus.AutoTimedOut);
        (await revocations.IsRevokedAsync(session.TokenId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldLeaveSessionActive_WhenRevocationThrows()
    {
        var revocations = new FailingRevocationCache(failsFor: _ => true);
        var (service, store, _, audit, clock, _) = CreateService(autoTimeoutMinutes: 10, revocations);
        var session = await StartSessionAsync(store, clock);
        clock.AdvanceMinutes(11);

        await service.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(session.Id, CancellationToken.None))!.Status.Should().Be(ImpersonationSessionStatus.Active,
            because: "a session closed while its token could not be revoked would leave the token working; the next tick retries");
        await audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default, default!, default!, default!, default!, default!, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldStillSweepOtherSessions_WhenOneTokenRevocationFails()
    {
        var store = new InMemoryImpersonationSessionStore();
        var clock = new TestClock(DateTimeOffset.Parse("2026-04-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var failing = MakeSession("sess-failing", ActorTenant, clock.GetUtcNow());
        var healthy = MakeSession("sess-healthy", ActorTenant, clock.GetUtcNow());
        var inner = new InMemoryJtiRevocationCache(clock);
        var revocations = new FailingRevocationCache(failsFor: jti => jti == failing.TokenId, inner);
        var (service, _, _, _, _, _) = CreateService(autoTimeoutMinutes: 10, revocations, store, clock);
        await store.AddAsync(failing, CancellationToken.None);
        await store.AddAsync(healthy, CancellationToken.None);
        clock.AdvanceMinutes(11);

        await service.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(failing.Id, CancellationToken.None))!.Status.Should().Be(ImpersonationSessionStatus.Active);
        (await store.GetAsync(healthy.Id, CancellationToken.None))!.Status.Should().Be(ImpersonationSessionStatus.AutoTimedOut);
        (await inner.IsRevokedAsync(healthy.TokenId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldUseTenantSpecificTimeout_WhenConfigured()
    {
        // Two tenants: actor-A has a tight 30-minute budget; actor-B keeps the
        // 4h default. Same elapsed time → only A's session is swept.
        var store = new InMemoryImpersonationSessionStore();
        var clock = new TestClock(DateTimeOffset.Parse("2026-04-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authConfigStore = Substitute.For<ITenantAuthConfigStore>();
        authConfigStore.GetAsync("tenant-A", Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig
            {
                TenantId = "tenant-A",
                ImpersonationAutoTimeoutMinutes = 30,
            });
        authConfigStore.GetAsync("tenant-B", Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig
            {
                TenantId = "tenant-B",
                // Defaults — 240 minutes.
            });

        var audit = Substitute.For<IAuditService>();
        var service = new ImpersonationSessionTimeoutService(
            store, authConfigStore, audit, new InMemoryJtiRevocationCache(clock),
            NullLogger<ImpersonationSessionTimeoutService>.Instance,
            clock);

        await store.AddAsync(MakeSession("sess-A", "tenant-A", clock.GetUtcNow()), CancellationToken.None);
        await store.AddAsync(MakeSession("sess-B", "tenant-B", clock.GetUtcNow()), CancellationToken.None);

        clock.AdvanceMinutes(60); // beyond A, well within B

        await service.SweepOnceAsync(CancellationToken.None);

        var a = await store.GetAsync("sess-A", CancellationToken.None);
        var b = await store.GetAsync("sess-B", CancellationToken.None);
        a!.Status.Should().Be(ImpersonationSessionStatus.AutoTimedOut);
        b!.Status.Should().Be(ImpersonationSessionStatus.Active);
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldEmitAuditEntry_OnAutoTimeout()
    {
        var (service, store, _, audit, clock, _) = CreateService(autoTimeoutMinutes: 60);

        var session = await StartSessionAsync(store, clock);
        clock.AdvanceMinutes(61);

        await service.SweepOnceAsync(CancellationToken.None);

        await audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == ActorTenant),
            "auth",
            "impersonation.session.auto_timeout",
            "warning",
            ActorUser,
            "system",
            session.Id,
            "ImpersonationSession",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m != null &&
                m["actor_tenant_id"] == ActorTenant
                && m["target_tenant_id"] == TargetTenant
                && m["timeout_minutes"] == "60"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (
        ImpersonationSessionTimeoutService Service,
        InMemoryImpersonationSessionStore Store,
        ITenantAuthConfigStore AuthConfigStore,
        IAuditService Audit,
        TestClock Clock,
        IJtiRevocationCache Revocations) CreateService(
            int autoTimeoutMinutes,
            IJtiRevocationCache? revocations = null,
            InMemoryImpersonationSessionStore? store = null,
            TestClock? clock = null)
    {
        store ??= new InMemoryImpersonationSessionStore();
        clock ??= new TestClock(DateTimeOffset.Parse("2026-04-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        revocations ??= new InMemoryJtiRevocationCache(clock);
        var authConfigStore = Substitute.For<ITenantAuthConfigStore>();
        authConfigStore.GetAsync(ActorTenant, Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig
            {
                TenantId = ActorTenant,
                ImpersonationAutoTimeoutMinutes = autoTimeoutMinutes,
            });
        var audit = Substitute.For<IAuditService>();
        var svc = new ImpersonationSessionTimeoutService(
            store, authConfigStore, audit, revocations,
            NullLogger<ImpersonationSessionTimeoutService>.Instance,
            clock);
        return (svc, store, authConfigStore, audit, clock, revocations);
    }

    private static async Task<ImpersonationSession> StartSessionAsync(
        InMemoryImpersonationSessionStore store, TestClock clock)
    {
        var session = MakeSession(Guid.NewGuid().ToString("N"), ActorTenant, clock.GetUtcNow());
        await store.AddAsync(session, CancellationToken.None);
        return session;
    }

    // The session's token lives 30 minutes from the start, as StartImpersonation mints it.
    private static ImpersonationSession MakeSession(string id, string actorTenant, DateTimeOffset startedAt) => new()
    {
        Id = id,
        TokenId = $"jti-{id}",
        TokenExpiresAt = startedAt.AddMinutes(30),
        ActorUserId = ActorUser,
        ActorTenantId = actorTenant,
        TargetTenantId = TargetTenant,
        StartedAt = startedAt,
    };

    /// <summary>Fails the revocation of every jti <paramref name="failsFor"/> selects (as an unreachable Redis does); passes the rest on.</summary>
    private sealed class FailingRevocationCache(Func<string, bool> failsFor, IJtiRevocationCache? inner = null) : IJtiRevocationCache
    {
        public ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct) =>
            inner?.IsRevokedAsync(jti, ct) ?? ValueTask.FromResult(false);

        public ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct) =>
            failsFor(jti)
                ? throw new TimeoutException("revocation store unreachable")
                : inner?.RevokeAsync(jti, expiresAt, ct) ?? ValueTask.CompletedTask;
    }

    /// <summary>Tiny ad-hoc replacement for <c>Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider</c>.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceMinutes(int minutes) => _now = _now.AddMinutes(minutes);
    }
}
