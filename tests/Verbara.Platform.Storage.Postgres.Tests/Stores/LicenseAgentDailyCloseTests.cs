using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.6; licensed-agent-daily-close, design D7) against a real
/// Postgres: idempotent closes, corrections, which tenants are closed, first-tick anchoring, the DST day cut,
/// the fixed day zone and the close's independence from licence state.
/// </summary>
public sealed class LicenseAgentDailyCloseTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly D1 = new(2026, 10, 1);
    private readonly AgentIdentityFixture _fixture;

    public LicenseAgentDailyCloseTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CloseAsync_ShouldWriteOneRow_WhenTwoWorkersCloseTheSameDay()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.CreateAgentAsync("c1", "a2");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Day1.AddDays(1).AddMinutes(5);

        await Task.WhenAll(
            h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None),
            h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None));

        (await h.DailyAsync("c1")).Should().ContainSingle().Which.Should().Match<LicenseAgentDaily>(
            d => d.Day == D1 && d.Revision == 0 && d.LicensedAgents == 2);
        (await h.DailyAsync(null)).Should().ContainSingle().Which.LicensedAgents.Should().Be(2);
    }

    [Fact]
    public async Task CloseAsync_ShouldAppendRevision1AndKeepRevision0_WhenALateRowFallsInAClosedDay()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Day1.AddDays(1).AddMinutes(5);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);
        var revision0 = (await h.DailyAsync("c1")).Single();

        // A replica whose clock lags commits a row stamped inside the closed day, after its close.
        h.Clock.UtcNow = Day1.AddHours(23);
        await h.CreateAgentAsync("c1", "a-late");
        h.Clock.UtcNow = Day1.AddDays(1).AddHours(1);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);

        var daily = await h.DailyAsync("c1");
        daily.Should().HaveCount(2);
        daily[0].Should().Be(revision0, "a closed day is never rewritten");
        daily[1].Should().Match<LicenseAgentDaily>(d => d.Day == D1 && d.Revision == 1 && d.LicensedAgents == 2);
        (await h.DailyAsync(null)).Select(d => (d.Revision, d.LicensedAgents)).Should().Equal((0, 1), (1, 2));
        LicenseAgentChain.Verify("c1", "lic-1", await h.EventsAsync("c1"), daily).Should().BeNull();
    }

    [Fact]
    public async Task CloseAsync_ShouldCloseEveryCustomerByType_AndNoPartnerOrPlatformTenant()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("platform", LicenseLedgerHarness.Platform);
        await h.TenantAsync("partner", LicenseLedgerHarness.Partner, parent: "platform");
        await h.TenantAsync("under-partner", LicenseLedgerHarness.Customer, parent: "partner");
        await h.TenantAsync("suspended", LicenseLedgerHarness.Customer, parent: "platform", status: (int)TenantStatus.Suspended);
        await h.TenantAsync("pending-deletion", LicenseLedgerHarness.Customer, parent: "platform", status: (int)TenantStatus.PendingDeletion);
        await h.TenantAsync("orphan-null", LicenseLedgerHarness.Customer, parent: null);
        await h.TenantAsync("orphan-missing", LicenseLedgerHarness.Customer, parent: "no-such-tenant");
        await h.CreateAgentAsync("partner", "p1");
        await h.CreateAgentAsync("partner", "p2");
        await h.CreateAgentAsync("platform", "x1");
        await h.CreateAgentAsync("under-partner", "c1");
        for (var i = 0; i < 4; i++) await h.CreateAgentAsync("suspended", $"s{i}");
        await h.CreateAgentAsync("pending-deletion", "d1");
        for (var i = 0; i < 3; i++) await h.CreateAgentAsync("orphan-null", $"o{i}");
        await h.CreateAgentAsync("orphan-missing", "m1");

        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Day1.AddDays(1).AddMinutes(1);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);

        async Task<int?> Day(string tenant) => (await h.DailyAsync(tenant)).SingleOrDefault()?.LicensedAgents;
        (await Day("under-partner")).Should().Be(1);
        (await Day("suspended")).Should().Be(4, "tenant status is never consulted");
        (await Day("pending-deletion")).Should().Be(1);
        (await Day("orphan-null")).Should().Be(3, "an orphaned Customer is closed");
        (await Day("orphan-missing")).Should().Be(1);
        (await h.DailyAsync("partner")).Should().BeEmpty("a Partner tenant is never closed");
        (await h.DailyAsync("platform")).Should().BeEmpty();
        (await h.EventsAsync("partner")).Should().NotBeEmpty("its changes are still in the exported ledger");
        (await h.DailyAsync(null)).Single().LicensedAgents.Should().Be(1 + 4 + 1 + 3 + 1, "the total excludes Partner and Platform agents");
    }

    [Fact]
    public async Task PrepareAsync_ShouldAnchorAQuietTenant_WhoseDaysAreThenClosedFromTheUpgradeOn()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("quiet", LicenseLedgerHarness.Customer);
        for (var i = 0; i < 6; i++)
        {
            await h.UserAsync("quiet", $"u{i}");
            await h.ExistingAgentAsync("quiet", $"a{i}", $"u{i}");
        }

        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Day1.AddDays(3).AddMinutes(1);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);

        var events = await h.EventsAsync("quiet");
        events[0].Kind.Should().Be(LicenseAgentEventKinds.ChainAnchored);
        events.Skip(1).Should().HaveCount(6).And.OnlyContain(e => e.Kind == LicenseAgentEventKinds.AgentBaseline);
        (await h.DailyAsync("quiet")).Select(d => (d.Day, d.LicensedAgents)).Should().Equal(
            (D1, 6), (D1.AddDays(1), 6), (D1.AddDays(2), 6));
    }

    [Fact]
    public async Task CloseAsync_ShouldCloseDaylightSavingDaysWhole_InTheDayZone()
    {
        var ny = LicenseAgentDayZone.Resolve("America/New_York");
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, new DateTimeOffset(2026, 3, 7, 15, 0, 0, TimeSpan.Zero));
        await h.TenantAsync("ny", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("ny", "base");
        await h.Close.PrepareAsync("America/New_York", CancellationToken.None);

        // 2026-03-09T03:30Z is 23:30 EDT on Mar 8 (the 23-hour day); a fixed 24-hour cut from 05:00Z
        // would also put 04:30Z (00:30 EDT Mar 9) in Mar 8.
        await CreateAndDeleteAsync(h, "ny", "spring-late", new DateTimeOffset(2026, 3, 9, 3, 30, 0, TimeSpan.Zero));
        await CreateAndDeleteAsync(h, "ny", "spring-next", new DateTimeOffset(2026, 3, 9, 4, 30, 0, TimeSpan.Zero));
        h.Clock.UtcNow = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        await h.Close.CloseAsync(ny, h.Clock.UtcNow, CancellationToken.None);

        var spring = (await h.DailyAsync("ny")).ToDictionary(d => d.Day, d => d.LicensedAgents);
        spring[new DateOnly(2026, 3, 8)].Should().Be(2, "23:30 EDT belongs to the 23-hour day");
        spring[new DateOnly(2026, 3, 9)].Should().Be(2, "00:30 EDT belongs to the next day");

        // 2026-11-02T04:30Z is 23:30 EST on Nov 1 (the 25-hour day); a 24-hour cut would end Nov 1 at 04:00Z.
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        await h.Close.CloseAsync(ny, h.Clock.UtcNow, CancellationToken.None);
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 4, 30, 0, TimeSpan.Zero);  // 23:30 EST Nov 1
        await h.CreateAgentAsync("ny", "fall-late-1");
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 4, 40, 0, TimeSpan.Zero);  // 23:40 EST Nov 1
        await h.CreateAgentAsync("ny", "fall-late-2");
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 4, 50, 0, TimeSpan.Zero);
        await h.Writer.CommitAgentDeletedAsync(new TenantId("ny"), EntityId.From("fall-late-1"), "admin", CancellationToken.None);
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 5, 10, 0, TimeSpan.Zero);  // 00:10 EST Nov 2
        await h.Writer.CommitAgentDeletedAsync(new TenantId("ny"), EntityId.From("fall-late-2"), "admin", CancellationToken.None);
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        await h.Close.CloseAsync(ny, h.Clock.UtcNow, CancellationToken.None);

        var fall = (await h.DailyAsync("ny")).Where(d => d.Revision == 0).ToDictionary(d => d.Day, d => d.LicensedAgents);
        fall[new DateOnly(2026, 11, 1)].Should().Be(3, "23:30 and 23:40 EST belong to the 25-hour day");
        fall[new DateOnly(2026, 11, 2)].Should().Be(2, "fall-late-2 is deleted at 00:10 EST Nov 2");
        LicenseAgentDayZone.Bounds(new DateOnly(2026, 3, 8), ny).Should().Be(
            (new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task PrepareAsync_ShouldRefuseAChangedDayZone_NamingBothAndWritingNothing()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        await h.TenantAsync("c2", LicenseLedgerHarness.Customer);   // would be anchored by the next first tick
        h.Licenses.CurrentLicenseId = "lic-renewed";               // would re-anchor the deployment chain
        var before = await h.CountAsync(
            "SELECT (SELECT COUNT(*) FROM license_agent_events) + (SELECT COUNT(*) FROM license_agent_daily) + (SELECT COUNT(*) FROM license_agent_chain_heads)");

        var act = () => h.Close.PrepareAsync("America/Bogota", CancellationToken.None);

        var error = (await act.Should().ThrowAsync<LicenseAgentDayZoneMismatchException>()).Which;
        error.Message.Should().Contain("'UTC'").And.Contain("'America/Bogota'").And.Contain(LicenseAgentDayZone.ConfigurationKey);
        (await h.CountAsync(
            "SELECT (SELECT COUNT(*) FROM license_agent_events) + (SELECT COUNT(*) FROM license_agent_daily) + (SELECT COUNT(*) FROM license_agent_chain_heads)"))
            .Should().Be(before, "nothing is anchored, re-anchored or closed");
    }

    [Fact]
    public async Task CloseAsync_ShouldStillClose_WhenNoLicenceIsLoaded()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9), licenseId: "lic-1");
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Licenses.CurrentLicenseId = null; // the licence is gone (expired beyond grace, removed)

        h.Clock.UtcNow = Day1.AddDays(1).AddMinutes(1);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);

        (await h.DailyAsync("c1")).Should().ContainSingle().Which.LicensedAgents.Should().Be(1);
        (await h.DailyAsync(null)).Should().ContainSingle();
    }

    [Fact]
    public async Task CloseAsync_ShouldCountAnAgentWhoseHistoryAgedOut_FromTheLiveRows()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        // The agent's rows are gone (the 15-month purge); the agent and its Active user remain.
        await h.ExecAsync("BEGIN; SET LOCAL verbara.license_purge = 'on'; DELETE FROM license_agent_events WHERE agent_id = 'a1'; COMMIT;");
        await h.UserAsync("c1", "u-gone", status: (int)UserStatus.Suspended);
        await h.ExistingAgentAsync("c1", "a-suspended-no-rows", "u-gone");

        h.Clock.UtcNow = Day1.AddDays(1).AddMinutes(1);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);

        (await h.DailyAsync("c1")).Single().LicensedAgents.Should().Be(1,
            "an agent that counts and has no retained row has not changed since its history aged out");
    }

    private static async Task CreateAndDeleteAsync(LicenseLedgerHarness h, string tenant, string agentId, DateTimeOffset at)
    {
        h.Clock.UtcNow = at;
        await h.CreateAgentAsync(tenant, agentId);
        h.Clock.UtcNow = at.AddMinutes(10);
        await h.Writer.CommitAgentDeletedAsync(new TenantId(tenant), EntityId.From(agentId), "admin", CancellationToken.None);
    }
}
