using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.Postgres.Licensing;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 3 (tasks.md 7.2/7.3; licensed-agent-reporting) against a real Postgres: a
/// month seeded through the real writer and daily close (creations, a suspension, a licence renewal, a late
/// row and its correction, a Partner tenant) is exported as contiguous chains that re-verify row by row from
/// their first exported row, end at their heads, and select exactly the rows the shared span rule selects;
/// the peaks read the latest revision of each day with the tenant names.
/// </summary>
public sealed class PostgresLicensedAgentReportReaderTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset Sep1 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly September1 = new(2026, 9, 1);
    private static readonly DateOnly September30 = new(2026, 9, 30);
    private readonly AgentIdentityFixture _fixture;

    public PostgresLicensedAgentReportReaderTests(AgentIdentityFixture fixture) => _fixture = fixture;

    // Ten days of September: c1 and c2 are Customers, p1 a Partner. Returns the harness at 2026-09-11 09:00.
    private async Task<LicenseLedgerHarness> SeedMonthAsync()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Sep1);
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.ExecAsync("UPDATE tenants SET name = 'Acme Support' WHERE tenant_id = 'c1'");
        await h.TenantAsync("c2", LicenseLedgerHarness.Customer);
        await h.TenantAsync("p1", LicenseLedgerHarness.Partner);
        await h.Close.PrepareAsync("UTC", CancellationToken.None);

        for (var day = 0; day < 10; day++)
        {
            h.Clock.UtcNow = Sep1.AddDays(day).AddHours(1);
            await h.CreateAgentAsync("c1", $"a{day}");
            if (day % 3 == 0)
                await h.CreateAgentAsync("c2", $"b{day}");
            if (day == 2)
                await h.CreateAgentAsync("p1", "p-agent");
            if (day == 4)
            {
                await h.Writer.CommitUserStatusChangedAsync(
                    new TenantId("c1"), EntityId.From("u-a1"), new AdminFieldsChange { Status = UserStatus.Suspended },
                    h.Clock.UtcNow, "admin", CancellationToken.None);
            }

            if (day == 6)
                h.Licenses.CurrentLicenseId = "lic-2";   // a renewal: every chain re-anchors at its next lock

            h.Clock.UtcNow = Sep1.AddDays(day + 1).AddMinutes(-9 * 60 + 5);   // 00:05 the next day
            await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);
        }

        // A late row inside 2026-09-05 (a replica with a lagging clock), corrected at the next close.
        h.Clock.UtcNow = Sep1.AddDays(4).AddHours(10);
        await h.CreateAgentAsync("c1", "late");
        h.Clock.UtcNow = Sep1.AddDays(10);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);
        return h;
    }

    private static PostgresLicensedAgentReportReader Reader(LicenseLedgerHarness h) => new(h.Ds);

    [Fact]
    public async Task ReadExportAsync_ShouldExportEveryChainWhole_VerifiedRowByRow_AndEndingAtItsHead()
    {
        var h = await SeedMonthAsync();

        var export = await Reader(h).ReadExportAsync(September1, September30, "America/Bogota", CancellationToken.None);

        export.DayZone.Should().Be("UTC", "the zone the deployment chain was anchored with wins over the configured fallback");
        export.ChainHeads.Select(c => c.TenantId).Should().Equal("c1", "c2", "p1", null);
        export.Daily.Should().Contain(d => d.TenantId == "c1" && d.Day == new DateOnly(2026, 9, 5) && d.Revision == 1,
            "every revision of a day is exported");
        export.Events.Should().Contain(e => e.TenantId == "p1", "the ledger has no tenant-type filter");
        foreach (var head in export.ChainHeads)
        {
            var events = export.Events.Where(e => e.TenantId == head.TenantId).ToList();
            var daily = export.Daily.Where(d => d.TenantId == head.TenantId).ToList();
            var sequences = events.Select(e => e.Sequence).Concat(daily.Select(d => d.Sequence)).Order().ToList();
            sequences.Should().Equal(Enumerable.Range(1, (int)head.HeadSequence).Select(i => (long)i),
                $"chain {head.TenantId ?? "deployment"} is exported whole, from its anchor to its head");
            LicenseAgentChain.Verify(head.TenantId, "lic-1", events, daily).Should().BeNull();
            var last = events.Select(e => (e.Sequence, e.RowHash)).Concat(daily.Select(d => (d.Sequence, d.RowHash))).MaxBy(r => r.Sequence);
            last.RowHash.Should().Be(head.HeadHash);
            head.LicenseId.Should().Be(head.TenantId == "p1" ? "lic-1" : "lic-2",
                "a chain re-anchors at its next lock after the renewal; the Partner chain was not locked since");
        }

        export.Events.Should().Contain(e => e.Kind == LicenseAgentEventKinds.ChainReanchored && e.LicenseId == "lic-2");
        (await h.CountAsync("SELECT count(*) FROM license_agent_events")).Should().Be(
            export.Events.Count, "reading writes nothing, and the month holds every row");
    }

    [Fact]
    public async Task ReadExportAsync_ShouldStartAtTheRowBeforeTheRange_AndSelectWhatTheSpanRuleSelects()
    {
        var h = await SeedMonthAsync();
        var from = new DateOnly(2026, 9, 4);
        var to = new DateOnly(2026, 9, 6);

        var export = await Reader(h).ReadExportAsync(from, to, "UTC", CancellationToken.None);

        var (start, end) = LicensedAgentExportSpan.Bounds(from, to, TimeZoneInfo.Utc);
        foreach (var head in export.ChainHeads)
        {
            var allEvents = await h.EventsAsync(head.TenantId);
            var allDaily = await h.DailyAsync(head.TenantId);
            var span = LicensedAgentExportSpan.Range(allEvents, allDaily, head.HeadSequence, start, end, from, to);

            var exported = export.Events.Where(e => e.TenantId == head.TenantId).Select(e => e.Sequence)
                .Concat(export.Daily.Where(d => d.TenantId == head.TenantId).Select(d => d.Sequence))
                .Order().ToList();
            var expected = span is { } s ? Enumerable.Range((int)s.First, (int)(s.Last - s.First + 1)).Select(i => (long)i).ToList() : [];
            exported.Should().Equal(expected, $"chain {head.TenantId ?? "deployment"} follows the shared span rule");

            if (exported.Count == 0)
                continue;
            var events = export.Events.Where(e => e.TenantId == head.TenantId).ToList();
            var daily = export.Daily.Where(d => d.TenantId == head.TenantId).ToList();
            LicenseAgentChain.Verify(head.TenantId, null, events, daily).Should().BeNull("the chain verifies from its first exported row");
            var firstOnOrAfterStart = allEvents.Where(e => e.OccurredAt >= start).Select(e => e.Sequence)
                .Concat(allDaily.Where(d => d.ClosedAt >= start).Select(d => d.Sequence))
                .DefaultIfEmpty(head.HeadSequence + 1).Min();
            exported[0].Should().Be(firstOnOrAfterStart - 1,
                "the row immediately before the range is included (the head, for a chain unchanged since before it)");
        }

        // The correction of 2026-09-05, written on 2026-09-11, is still exported with every row before it.
        export.Daily.Should().Contain(d => d.TenantId == "c1" && d.Day == new DateOnly(2026, 9, 5) && d.Revision == 1);
        export.Events.Where(e => e.TenantId == "p1").Select(e => e.Sequence).Should().Equal(
            [export.ChainHeads.Single(c => c.TenantId == "p1").HeadSequence], "the Partner chain did not change in the range");
    }

    [Fact]
    public async Task ReadExportAsync_ShouldAccept15CalendarMonths_AndReturnTheSameRowsAsTheMonth()
    {
        var h = await SeedMonthAsync();

        var month = await Reader(h).ReadExportAsync(September1, September30, "UTC", CancellationToken.None);
        var fifteen = await Reader(h).ReadExportAsync(new DateOnly(2025, 7, 1), September30, "UTC", CancellationToken.None);

        fifteen.Events.Select(e => (e.TenantId, e.Sequence)).Should().Equal(month.Events.Select(e => (e.TenantId, e.Sequence)));
        fifteen.Daily.Select(d => (d.TenantId, d.Sequence)).Should().Equal(month.Daily.Select(d => (d.TenantId, d.Sequence)));
    }

    [Fact]
    public async Task ReadExportAsync_ShouldCarryOnlyTheHeadRow_OfAChainUnchangedSinceBeforeTheRange()
    {
        var h = await SeedMonthAsync();

        var export = await Reader(h).ReadExportAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "UTC", CancellationToken.None);

        foreach (var head in export.ChainHeads)
        {
            var rows = export.Events.Where(e => e.TenantId == head.TenantId).Select(e => (e.Sequence, e.RowHash))
                .Concat(export.Daily.Where(d => d.TenantId == head.TenantId).Select(d => (d.Sequence, d.RowHash)))
                .ToList();
            rows.Should().Equal([(head.HeadSequence, head.HeadHash)], "the verifier still matches the head");
        }
    }

    [Fact]
    public async Task ReadPeaksAsync_ShouldReadTheLatestRevisionOfEachDay_WithTenantNamesAndHeads()
    {
        var h = await SeedMonthAsync();

        var peaks = await Reader(h).ReadPeaksAsync(September1, September30, "America/Bogota", CancellationToken.None);

        peaks.DayZone.Should().Be("UTC");
        peaks.Daily.Should().OnlyContain(d => d.TenantId == null || d.TenantId == "c1" || d.TenantId == "c2",
            "only Customer tenants and the deployment total are closed");
        peaks.Daily.GroupBy(d => (d.TenantId, d.Day)).Should().OnlyContain(g => g.Count() == 1, "one row per chain and day");
        var corrected = peaks.Daily.Single(d => d.TenantId == "c1" && d.Day == new DateOnly(2026, 9, 5));
        corrected.Revision.Should().Be(1);
        corrected.LicensedAgents.Should().Be(5, "a0..a4 and the late agent, with a1 suspended");
        peaks.Daily.Where(d => d.TenantId is null).Select(d => d.Day).Should().HaveCount(10);
        peaks.TenantNames.Should().Contain("c1", "Acme Support").And.Contain("c2", "c2");
        peaks.ChainHeads.Select(c => c.TenantId).Should().Equal("c1", "c2", "p1", null);
    }

    [Fact]
    public async Task Reads_ShouldUseTheConfiguredZone_WhileTheDeploymentChainIsNotAnchored()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Sep1);

        var peaks = await Reader(h).ReadPeaksAsync(September1, September30, "America/Bogota", CancellationToken.None);
        var export = await Reader(h).ReadExportAsync(September1, September30, "America/Bogota", CancellationToken.None);

        peaks.DayZone.Should().Be("America/Bogota");
        peaks.Daily.Should().BeEmpty();
        export.DayZone.Should().Be("America/Bogota");
        export.Events.Should().BeEmpty();
        export.ChainHeads.Should().BeEmpty();
    }
}
