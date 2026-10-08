using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.Postgres.Licensing;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.7; licensed-agent-daily-close, design D8) against a real
/// Postgres: the fixed 15-month purge runs for every tenant whatever its retention policy, never shortens,
/// never touches younger rows, and leaves a <c>purge_log</c> tombstone the remaining chain verifies from.
/// </summary>
public sealed class LicenseAgentRetentionPurgeTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset Start = new(2025, 1, 10, 9, 0, 0, TimeSpan.Zero);
    private readonly AgentIdentityFixture _fixture;

    public LicenseAgentRetentionPurgeTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PurgeAsync_ShouldPurgeRowsOlderThan15Months_ForATenantWithoutAPolicy_AndLeaveATombstone()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Start);
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a-old");                       // 2025-01-10: purged at 16 months
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Start.AddDays(2);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);
        h.Clock.UtcNow = Start.AddMonths(2);                           // 2025-03-10: 14 months old at the purge
        await h.CreateAgentAsync("c1", "a-young");
        var before = await h.EventsAsync("c1");
        var beforeDaily = await h.DailyAsync("c1");
        (await h.CountAsync("SELECT COUNT(*) FROM tenant_retention_policies")).Should().Be(0);

        var now = Start.AddMonths(16);                                 // 2026-05-10: the cut is 2025-02-10
        h.Clock.UtcNow = now;
        var purged = await h.Purge.PurgeAsync(TimeZoneInfo.Utc, now, CancellationToken.None);

        purged.Should().BeGreaterThan(0);
        var events = await h.EventsAsync("c1");
        var daily = await h.DailyAsync("c1");
        events.Should().OnlyContain(e => e.OccurredAt >= Start.AddMonths(1), "everything older than 15 months is gone");
        events.Should().Contain(e => e.AgentId == "a-young", "rows younger than 15 months are never purged");
        daily.Should().BeEmpty("the closed days of January 2025 are older than the window");

        var lastPurged = before.Select(e => (e.Sequence, e.RowHash)).Concat(beforeDaily.Select(d => (d.Sequence, d.RowHash)))
            .Where(r => r.Sequence < events.Min(e => e.Sequence)).MaxBy(r => r.Sequence);
        var tombstone = await TombstoneAsync(h, "c1");
        tombstone.SubjectType.Should().Be(PostgresLicenseAgentRetentionPurge.SubjectType);
        tombstone.SubjectId.Should().Be("c1");
        tombstone.Reason.Should().Contain($"through sequence {lastPurged.Sequence} {lastPurged.RowHash}");
        tombstone.Counts["events"].Should().Be(before.Count - events.Count);
        tombstone.Counts["daily"].Should().Be(beforeDaily.Count);
        tombstone.Counts["throughSequence"].Should().Be((int)lastPurged.Sequence);
        events[0].PrevHash.Should().Be(lastPurged.RowHash, "verification of the remaining chain starts from the tombstone");
        LicenseAgentChain.Verify("c1", "lic-1", events, daily).Should().BeNull();
    }

    [Fact]
    public async Task PurgeAsync_ShouldNotShortenTheWindow_WhenTheTenantKeepsAuditEntriesFor30Days()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Start);
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.ExecAsync("INSERT INTO tenant_retention_policies (tenant_id, audit_retention_days, conversation_retention_days, " +
                          "auth_event_retention_days, usage_record_retention_days) VALUES ('c1', 30, 30, 30, 30)");
        await h.CreateAgentAsync("c1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Start.AddDays(1);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);
        var events = await h.EventsAsync("c1");
        var daily = await h.DailyAsync("c1");

        // 60 days later: the tenant's 30-day retention runs every store RetentionPurgeService purges, then the
        // ledger purge runs. Neither touches the ledger.
        var now = Start.AddDays(60);
        var tid = new TenantId("c1");
        await new PostgresAuditStore(h.Ds).DeleteOlderThanAsync(tid, now.AddDays(-30), CancellationToken.None);
        await new PostgresConversationStore(h.Ds).DeleteOlderThanAsync(tid, now.AddDays(-30), CancellationToken.None);
        await new PostgresAuthEventStore(h.Ds).DeleteOlderThanAsync("c1", now.AddDays(-30), CancellationToken.None);
        await new PostgresUsageRecordStore(h.Ds).DeleteOlderThanAsync(tid, now.AddDays(-30), CancellationToken.None);
        (await h.Purge.PurgeAsync(TimeZoneInfo.Utc, now, CancellationToken.None)).Should().Be(0);

        (await h.EventsAsync("c1")).Should().BeEquivalentTo(events, o => o.WithStrictOrdering());
        (await h.DailyAsync("c1")).Should().BeEquivalentTo(daily, o => o.WithStrictOrdering());
        (await h.CountAsync("SELECT COUNT(*) FROM purge_log WHERE subject_type = 'license_agent'")).Should().Be(0);
    }

    [Fact]
    public async Task PurgeAsync_ShouldPurgeTheDeploymentChainToo_WithItsOwnTombstone()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Start);
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        h.Clock.UtcNow = Start.AddDays(3);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);
        (await h.DailyAsync(null)).Should().NotBeEmpty();

        var now = Start.AddMonths(16);
        await h.Purge.PurgeAsync(TimeZoneInfo.Utc, now, CancellationToken.None);

        (await h.DailyAsync(null)).Should().BeEmpty();
        (await TombstoneAsync(h, LicenseAgentChain.DeploymentChainKey)).SubjectType.Should().Be("license_agent");
    }

    private sealed record Tombstone(string SubjectType, string SubjectId, string Reason, Dictionary<string, int> Counts);

    private static async Task<Tombstone> TombstoneAsync(LicenseLedgerHarness h, string chainKey)
    {
        await using var cmd = h.Ds.CreateCommand(
            "SELECT subject_type, subject_id, reason, entities_deleted::text FROM purge_log WHERE subject_id = @K");
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("K", chainKey));
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the purge writes a purge_log row per chain it purged");
        var counts = JsonSerializer.Deserialize(reader.GetString(3), PostgresJson.Ctx.DictionaryStringInt32)!;
        return new Tombstone(reader.GetString(0), reader.GetString(1), reader.GetString(2), counts);
    }
}
