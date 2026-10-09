using Npgsql;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.Postgres.Licensing;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 4.1, 5.1; licensed-agent-ledger, design D6) against a real
/// Postgres: migration <c>019_LicenseAgentLedger.sql</c>'s immutability triggers and its once-only day zone,
/// and the ledger's head lock, anchoring, baseline and re-anchor.
/// </summary>
public sealed class LicenseAgentLedgerTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly AgentIdentityFixture _fixture;

    public LicenseAgentLedgerTests(AgentIdentityFixture fixture) => _fixture = fixture;

    // ─── 4.1 migration 019 ──────────────────────────────────────────────────────

    [Fact]
    public async Task LedgerRows_ShouldRejectUpdate_OnEventsAndDailyRows()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.TenantAsync("t1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("t1", "a1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, T0.AddDays(1), CancellationToken.None);
        var before = await h.EventsAsync("t1");

        var updateEvent = () => h.ExecAsync("UPDATE license_agent_events SET counted = true");
        var updateDaily = () => h.ExecAsync("UPDATE license_agent_daily SET licensed_agents = 99");

        (await updateEvent.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        (await updateDaily.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        (await h.EventsAsync("t1")).Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
        (await h.CountAsync("SELECT COUNT(*) FROM license_agent_daily WHERE licensed_agents = 99")).Should().Be(0);
    }

    [Fact]
    public async Task LedgerRows_ShouldRejectDelete_WithoutThePurgeFlag()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.CreateAgentAsync("t1", "a1");

        var delete = () => h.ExecAsync("DELETE FROM license_agent_events");
        var deleteOff = () => h.ExecAsync(
            "BEGIN; SET LOCAL verbara.license_purge = 'off'; DELETE FROM license_agent_events; COMMIT;");

        await delete.Should().ThrowAsync<PostgresException>();
        await deleteOff.Should().ThrowAsync<PostgresException>();
        (await h.CountAsync("SELECT COUNT(*) FROM license_agent_events")).Should().Be(2);
    }

    [Fact]
    public async Task LedgerRows_ShouldAllowDelete_UnderSetLocalPurgeFlag_OnlyInThatTransaction()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.CreateAgentAsync("t1", "a1");

        await using (var conn = await h.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var set = new NpgsqlCommand("SET LOCAL verbara.license_purge = 'on'", conn, tx))
                await set.ExecuteNonQueryAsync();
            await using (var del = new NpgsqlCommand("DELETE FROM license_agent_events WHERE sequence = 2", conn, tx))
                (await del.ExecuteNonQueryAsync()).Should().Be(1);
            await tx.CommitAsync();
        }

        var afterwards = () => h.ExecAsync("DELETE FROM license_agent_events");
        await afterwards.Should().ThrowAsync<PostgresException>("SET LOCAL ends with its transaction");
    }

    [Fact]
    public async Task DayZone_ShouldBeWrittenOnceOnTheDeploymentHead_AndNeverChange()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);

        await h.AnchorAsync(null, dayZone: "America/Bogota");
        await h.AnchorAsync("t1", dayZone: "Europe/Madrid");
        h.Licenses.CurrentLicenseId = "lic-2";                  // a later head update (re-anchor) ...
        await h.AnchorAsync(null, dayZone: "UTC");              // ... with another zone passed in

        (await h.ScalarTextAsync("SELECT day_zone FROM license_agent_chain_heads WHERE chain_key = '*deployment'"))
            .Should().Be("America/Bogota");
        (await h.ScalarTextAsync("SELECT day_zone FROM license_agent_chain_heads WHERE chain_key = 't1'"))
            .Should().BeNull("a tenant head never carries the day zone");
        (await h.ScalarTextAsync("SELECT license_id FROM license_agent_chain_heads WHERE chain_key = '*deployment'"))
            .Should().Be("lic-2", "the head did move");

        var rewrite = () => h.ExecAsync("UPDATE license_agent_chain_heads SET day_zone = 'UTC' WHERE chain_key = '*deployment'");
        var tenantZone = () => h.ExecAsync("UPDATE license_agent_chain_heads SET day_zone = 'UTC' WHERE chain_key = 't1'");
        (await rewrite.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("fixed at America/Bogota");
        await tenantZone.Should().ThrowAsync<PostgresException>();
    }

    // ─── 5.1 the ledger ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendAsync_ShouldStayGapFreeAndUnforked_WhenTransactionsAppendConcurrently()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.AnchorAsync("t1");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            await using var conn = await h.Ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var chain = await h.Ledger.LockAsync(conn, tx, "t1", CancellationToken.None);
            await h.Ledger.AppendEventAsync(conn, tx, chain,
                new LicenseAgentChange(LicenseAgentEventKinds.AgentCreated, $"a{i}", $"u{i}", "admin", null, null, true),
                CancellationToken.None);
            await tx.CommitAsync();
        }));

        var rows = await h.EventsAsync("t1");
        rows.Select(r => r.Sequence).Should().Equal(Enumerable.Range(1, 25).Select(i => (long)i));
        LicenseAgentChain.Verify("t1", "lic-1", rows, []).Should().BeNull("every row links to its predecessor");
        (await h.CountAsync("SELECT head_sequence FROM license_agent_chain_heads WHERE chain_key = 't1'")).Should().Be(25);
    }

    [Fact]
    public async Task LockAsync_ShouldReanchorLinkedToTheOldHead_WhenTheLicenceIsRenewed()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0, licenseId: "lic-L1");
        await h.CreateAgentAsync("t1", "a1");
        var oldHead = (await h.EventsAsync("t1"))[^1].RowHash;

        h.Licenses.CurrentLicenseId = "lic-L2";
        await h.CreateAgentAsync("t1", "a2");

        var rows = await h.EventsAsync("t1");
        rows.Select(r => r.Kind).Should().Equal(
            LicenseAgentEventKinds.ChainAnchored, LicenseAgentEventKinds.AgentCreated,
            LicenseAgentEventKinds.ChainReanchored, LicenseAgentEventKinds.AgentCreated);
        rows[2].PrevHash.Should().Be(oldHead);
        rows[2].LicenseId.Should().Be("lic-L2");
        rows[3].LicenseId.Should().Be("lic-L2");
        rows[0].PrevHash.Should().Be(LicenseAgentChain.Genesis("t1", "lic-L1"), "a renewal never starts a new genesis");
        LicenseAgentChain.Verify("t1", "lic-L1", rows, []).Should().BeNull();
    }

    [Fact]
    public async Task LockAsync_ShouldBaselineEveryExistingAgent_WhenTheChainIsFirstAnchored()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.UserAsync("t1", "u-active", status: 0);
        await h.UserAsync("t1", "u-suspended", status: 1);
        await h.ExistingAgentAsync("t1", "a-2", "u-active");
        await h.ExistingAgentAsync("t1", "a-1", "u-suspended");
        await h.ExistingAgentAsync("t1", "a-3", "u-missing");

        await h.AnchorAsync("t1");

        var rows = await h.EventsAsync("t1");
        rows.Select(r => (r.Kind, r.AgentId, r.Counted)).Should().Equal(
            (LicenseAgentEventKinds.ChainAnchored, null, null),
            (LicenseAgentEventKinds.AgentBaseline, "a-1", false),
            (LicenseAgentEventKinds.AgentBaseline, "a-2", true),
            (LicenseAgentEventKinds.AgentBaseline, "a-3", false));
    }

    [Fact]
    public async Task AnchorAsync_ShouldAnchorOnceWithOneBaseline_WhenItRacesAFirstAppend()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        for (var round = 0; round < 8; round++)
        {
            var tenant = $"race-{round}";
            await h.TenantAsync(tenant, LicenseLedgerHarness.Customer);
            await h.UserAsync(tenant, "u-old");
            await h.ExistingAgentAsync(tenant, "a-old", "u-old");
            await h.UserAsync(tenant, "u-new");

            await Task.WhenAll(
                h.Close.PrepareAsync("UTC", CancellationToken.None),
                h.Writer.CommitAgentCreatedAsync(LicenseLedgerHarness.NewAgent(tenant, "a-new", "u-new"), "admin", CancellationToken.None));

            var kinds = (await h.EventsAsync(tenant)).Select(r => r.Kind).ToList();
            kinds.Count(k => k == LicenseAgentEventKinds.ChainAnchored).Should().Be(1);
            kinds.Count(k => k == LicenseAgentEventKinds.AgentBaseline).Should().Be(1, "only the agent that existed before is baselined");
            kinds.Count(k => k == LicenseAgentEventKinds.AgentCreated).Should().Be(1);
            LicenseAgentChain.Verify(tenant, "lic-1", await h.EventsAsync(tenant), []).Should().BeNull();
        }
    }

    [Fact]
    public async Task LockAsync_ShouldAnchorWithTheNullForm_WhenNoLicenceIsLoaded()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0, licenseId: null);

        await h.CreateAgentAsync("t1", "a1");

        var rows = await h.EventsAsync("t1");
        rows[0].PrevHash.Should().Be(LicenseAgentChain.Genesis("t1", null));
        rows.Should().OnlyContain(r => r.LicenseId == null);
        LicenseAgentChain.Verify("t1", null, rows, []).Should().BeNull();
    }
}
