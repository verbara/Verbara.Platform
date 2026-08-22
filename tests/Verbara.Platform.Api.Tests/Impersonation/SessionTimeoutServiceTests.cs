using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
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
        var (service, store, _, _, clock) = CreateService(autoTimeoutMinutes: 240);

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
        var (service, store, _, _, clock) = CreateService(autoTimeoutMinutes: 240);

        var session = await StartSessionAsync(store, clock);

        // Halfway through the budget — must remain active.
        clock.AdvanceMinutes(120);

        await service.SweepOnceAsync(CancellationToken.None);

        var refreshed = await store.GetAsync(session.Id, CancellationToken.None);
        refreshed.Should().NotBeNull();
        refreshed!.Status.Should().Be(ImpersonationSessionStatus.Active);
        refreshed.EndedAt.Should().BeNull();
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
            store, authConfigStore, audit,
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
        var (service, store, _, audit, clock) = CreateService(autoTimeoutMinutes: 60);

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
        TestClock Clock) CreateService(int autoTimeoutMinutes)
    {
        var store = new InMemoryImpersonationSessionStore();
        var clock = new TestClock(DateTimeOffset.Parse("2026-04-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authConfigStore = Substitute.For<ITenantAuthConfigStore>();
        authConfigStore.GetAsync(ActorTenant, Arg.Any<CancellationToken>())
            .Returns(new TenantAuthConfig
            {
                TenantId = ActorTenant,
                ImpersonationAutoTimeoutMinutes = autoTimeoutMinutes,
            });
        var audit = Substitute.For<IAuditService>();
        var svc = new ImpersonationSessionTimeoutService(
            store, authConfigStore, audit,
            NullLogger<ImpersonationSessionTimeoutService>.Instance,
            clock);
        return (svc, store, authConfigStore, audit, clock);
    }

    private static async Task<ImpersonationSession> StartSessionAsync(
        InMemoryImpersonationSessionStore store, TestClock clock)
    {
        var session = MakeSession(Guid.NewGuid().ToString("N"), ActorTenant, clock.GetUtcNow());
        await store.AddAsync(session, CancellationToken.None);
        return session;
    }

    private static ImpersonationSession MakeSession(string id, string actorTenant, DateTimeOffset startedAt) => new()
    {
        Id = id,
        ActorUserId = ActorUser,
        ActorTenantId = actorTenant,
        TargetTenantId = TargetTenant,
        StartedAt = startedAt,
    };

    /// <summary>Tiny ad-hoc replacement for <c>Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider</c>.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceMinutes(int minutes) => _now = _now.AddMinutes(minutes);
    }
}
