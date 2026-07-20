using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Platform.Storage.Postgres;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Sdk.Pro.Dialer.Diagnostics;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Live-DB coverage for <see cref="PostgresDialerLicenseAuditSink"/> (dialer-license-audit-sink,
/// Pro/ADR-0016). Round-trips each <see cref="DialerLicenseAuditRecord"/> through the real
/// <c>dialer_license_audit</c> table (migration 017 applied from disk): the golden
/// <c>CampaignsQuiesced</c> episode, the <c>Recovered</c> event's null/empty fields, the
/// null license-identity fields (no <c>42P08</c>), and the jsonb <c>Campaigns</c> round-trip.
/// The fail-safe contract (must not throw into the dial path) is exercised without a live DB.
/// </summary>
[Collection("DialerLicenseAudit")]
public sealed class PostgresDialerLicenseAuditSinkTests : IAsyncLifetime
{
    private readonly DialerLicenseAuditFixture _fixture;
    private readonly PostgresDialerLicenseAuditSink _sink;

    private static readonly Guid Engine = new("b7c2f4a0-9e31-4d5a-8c6b-2f1e0a9d3c47");
    private static readonly DateTimeOffset Occurred =
        new(2026, 7, 20, 14, 3, 52, 117, TimeSpan.Zero);

    public PostgresDialerLicenseAuditSinkTests(DialerLicenseAuditFixture fixture)
    {
        _fixture = fixture;
        _sink = new PostgresDialerLicenseAuditSink(
            _fixture.DataSource, NullLogger<PostgresDialerLicenseAuditSink>.Instance);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // The golden fixture (fixtures/dialer-license-audit-record.v1.json) verbatim.
    private static DialerLicenseAuditRecord GoldenQuiescedRecord() => new()
    {
        SchemaVersion = 1,
        Event = DialerLicenseAuditEvent.CampaignsQuiesced,
        OccurredAt = Occurred,
        TickSequence = 48213,
        EngineInstanceId = Engine,
        Reason = LicenseBlockReason.Revoked,
        ReasonSequence = "NotLicensed,Revoked",
        ConsecutiveBlockedTicks = 6,
        Campaigns =
        [
            new QuiescedCampaignInfo(90142, "acme", "Q3 Outbound Winback"),
            new QuiescedCampaignInfo(90188, "acme", "Renewals Nudge"),
        ],
        InFlightAtQuiesce = 3,
        LicenseId = "lic_7fK29ab",
        Licensee = "Acme Contact Center S.A.",
        Tier = LicenseTier.SelfHostBusiness,
        CampaignsRebuilt = 0,
    };

    [Fact]
    public async Task RecordAsync_ShouldPersistScalarColumns_WhenCampaignsQuiescedEpisode()
    {
        var record = GoldenQuiescedRecord();

        await _sink.RecordAsync(record, CancellationToken.None);

        (await _fixture.CountRowsAsync()).Should().Be(1);
        var row = await _fixture.ReadSingleRowAsync();
        row.SchemaVersion.Should().Be(record.SchemaVersion);
        row.Event.Should().Be("CampaignsQuiesced");
        row.OccurredAt.Should().Be(record.OccurredAt);
        row.TickSequence.Should().Be(record.TickSequence);
        row.EngineInstanceId.Should().Be(record.EngineInstanceId);
        row.Reason.Should().Be("Revoked");
        row.ReasonSequence.Should().Be("NotLicensed,Revoked");
        row.ConsecutiveBlockedTicks.Should().Be(record.ConsecutiveBlockedTicks);
        row.InFlightAtQuiesce.Should().Be(record.InFlightAtQuiesce);
        row.LicenseId.Should().Be(record.LicenseId);
        row.Licensee.Should().Be(record.Licensee);
        row.Tier.Should().Be("SelfHostBusiness");
        row.CampaignsRebuilt.Should().Be(record.CampaignsRebuilt);
    }

    [Fact]
    public async Task RecordAsync_ShouldRoundTripCampaignsAsJsonb_WhenTwoCampaigns()
    {
        var record = GoldenQuiescedRecord();

        await _sink.RecordAsync(record, CancellationToken.None);

        var row = await _fixture.ReadSingleRowAsync();
        using var doc = JsonDocument.Parse(row.CampaignsJson);
        var items = doc.RootElement;
        items.ValueKind.Should().Be(JsonValueKind.Array);
        items.GetArrayLength().Should().Be(2);

        var first = items[0];
        first.GetProperty("campaignId").GetInt64().Should().Be(90142);
        first.GetProperty("tenantId").GetString().Should().Be("acme");
        first.GetProperty("name").GetString().Should().Be("Q3 Outbound Winback");

        var second = items[1];
        second.GetProperty("campaignId").GetInt64().Should().Be(90188);
        second.GetProperty("tenantId").GetString().Should().Be("acme");
        second.GetProperty("name").GetString().Should().Be("Renewals Nudge");
    }

    [Fact]
    public async Task RecordAsync_ShouldPersistNullReasonAndEmptyCampaigns_WhenRecoveredEvent()
    {
        var record = new DialerLicenseAuditRecord
        {
            SchemaVersion = 1,
            Event = DialerLicenseAuditEvent.Recovered,
            OccurredAt = Occurred,
            TickSequence = 48260,
            EngineInstanceId = Engine,
            Reason = null,
            ReasonSequence = null,
            ConsecutiveBlockedTicks = 0,
            Campaigns = Array.Empty<QuiescedCampaignInfo>(),
            InFlightAtQuiesce = 0,
            LicenseId = "lic_7fK29ab",
            Licensee = "Acme Contact Center S.A.",
            Tier = LicenseTier.SelfHostBusiness,
            CampaignsRebuilt = 4,
        };

        await _sink.RecordAsync(record, CancellationToken.None);

        var row = await _fixture.ReadSingleRowAsync();
        row.Event.Should().Be("Recovered");
        row.Reason.Should().BeNull();
        row.ReasonSequence.Should().BeNull();
        row.CampaignsRebuilt.Should().Be(4);

        using var doc = JsonDocument.Parse(row.CampaignsJson);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task RecordAsync_ShouldPersistNullLicenseIdentity_WithoutBindError()
    {
        // Null LicenseId + Licensee must persist as SQL NULL with no 42P08 (indeterminate
        // parameter type) — the explicit NpgsqlDbType.Text on the nullable params.
        var record = GoldenQuiescedRecord() with
        {
            LicenseId = null,
            Licensee = null,
        };

        var act = async () => await _sink.RecordAsync(record, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var row = await _fixture.ReadSingleRowAsync();
        row.LicenseId.Should().BeNull();
        row.Licensee.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_ShouldSwallowAndLog_WhenInsertFaults()
    {
        // Contract (design D5): the sink must not throw into the dial path. A closed/disposed data
        // source faults the INSERT; RecordAsync must catch, log, and return normally. No live DB
        // needed — this exercises the fail-safe boundary directly.
        var deadDataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Password=nobody;Timeout=1;Command Timeout=1");
        await deadDataSource.DisposeAsync();

        var sink = new PostgresDialerLicenseAuditSink(
            deadDataSource, NullLogger<PostgresDialerLicenseAuditSink>.Instance);

        var act = async () => await sink.RecordAsync(GoldenQuiescedRecord(), CancellationToken.None);

        await act.Should().NotThrowAsync(
            because: "the sink contract requires it never throws into the dial path (design D5)");
    }

    [Fact]
    public void GetService_ShouldResolveNonNullPostgresSink_AfterStorageRegistration()
    {
        // The seam the Pro DialerEngine reads via GetService<IDialerLicenseAuditSink>() — null before
        // this change, a non-null PostgresDialerLicenseAuditSink after the Storage.Postgres
        // registrations run (design D4, spec Requirement 4).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresStorage(
            "Host=localhost;Database=test;Username=postgres;Password=postgres");
        var provider = services.BuildServiceProvider();

        var sink = provider.GetService<IDialerLicenseAuditSink>();
        sink.Should().NotBeNull();
        sink.Should().BeOfType<PostgresDialerLicenseAuditSink>();
    }
}
