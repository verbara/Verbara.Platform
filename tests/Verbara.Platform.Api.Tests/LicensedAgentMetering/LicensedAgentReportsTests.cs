using System.Globalization;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering slice 3 (tasks.md 7.2/7.3; licensed-agent-reporting): the pure rules behind both
/// reports — the range rule (at most 460 days, so 15 calendar months fit one call), the server-side
/// <c>overBand</c>, the peak over the latest revision of each day, and the export's chain order.
/// </summary>
public sealed class LicensedAgentReportsTests
{
    private static readonly LicensedAgentLicenseDto Undeclared = new(null, null, "None", null, true);

    [Theory]
    [InlineData("2026-09-01", "2026-09-30", true)]
    [InlineData("2026-09-17", "2026-09-17", true)]
    [InlineData("2025-07-01", "2026-09-30", true)]   // 15 calendar months: 457 days
    [InlineData("2024-01-01", "2025-04-04", true)]   // 460 days
    [InlineData("2024-01-01", "2025-04-05", false)]  // 461 days
    [InlineData("2026-09-30", "2026-09-01", false)]  // from later than to
    [InlineData(null, "2026-09-30", false)]
    [InlineData("2026-09-01", null, false)]
    [InlineData("", "2026-09-30", false)]
    [InlineData("2026-9-1", "2026-09-30", false)]
    [InlineData("2026-02-30", "2026-03-01", false)]
    [InlineData("2026-09-01T00:00:00Z", "2026-09-30", false)]
    public void ParseRange_ShouldAcceptAtMost460Days_OfValidIsoDates(string? from, string? to, bool accepted)
    {
        LicensedAgentReports.ParseRange(from, to).HasValue.Should().Be(accepted);
    }

    [Fact]
    public void ParseRange_ShouldAcceptEvery15CalendarMonthWindow()
    {
        // The longest 15 consecutive calendar months span 458 days; every one must fit a single call.
        for (var start = new DateOnly(2024, 1, 1); start < new DateOnly(2028, 1, 1); start = start.AddMonths(1))
        {
            var end = start.AddMonths(15).AddDays(-1);
            LicensedAgentReports.ParseRange(
                    start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Should().Be((start, end), $"{start}..{end} is 15 calendar months");
        }
    }

    [Theory]
    [InlineData(null, 120, false)]
    [InlineData(0, 120, false)]
    [InlineData(50, 0, false)]
    [InlineData(50, 50, false)]
    [InlineData(50, 51, true)]
    [InlineData(50, 63, true)]
    public void IsOverBand_ShouldBeTrueOnlyAboveADeclaredBand(int? maxAgents, int peak, bool overBand)
    {
        LicensedAgentReports.IsOverBand(maxAgents, peak).Should().Be(overBand);
    }

    private static LicenseAgentDaily Daily(string? tenantId, DateOnly day, int agents, int revision = 0, string? hash = null) => new()
    {
        TenantId = tenantId,
        Sequence = 1,
        Day = day,
        Revision = revision,
        LicensedAgents = agents,
        ClosedAt = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1),
        ClosedThroughSequence = 0,
        LicenseId = null,
        PrevHash = "lac1:prev",
        RowHash = hash ?? $"lac1:{tenantId ?? "deployment"}-{day:yyyyMMdd}-{revision}",
    };

    private static LicensedAgentPeaksResponse Peaks(IEnumerable<LicenseAgentDaily> rows, LicensedAgentLicenseDto? license = null,
        IReadOnlyDictionary<string, LicensedAgentTenantRef>? tenants = null) =>
        LicensedAgentReports.BuildPeaks(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            new LicensedAgentPeaksData("UTC", rows.ToList(), tenants ?? new Dictionary<string, LicensedAgentTenantRef>(), []),
            license ?? Undeclared);

    private static LicensedAgentTenantRef Ref(string name, string type, string? parent) => new(name, type, parent);

    [Fact]
    public void BuildPeaks_ShouldReportTheLatestRevisionOfEachDay()
    {
        var d = new DateOnly(2026, 9, 17);
        var response = Peaks([
            Daily(null, d, 40), Daily(null, d, 47, revision: 1),
            Daily("c1", d, 31), Daily("c1", d, 32, revision: 1), Daily("c2", d, 15),
        ]);

        var day = response.Days.Should().ContainSingle().Subject;
        day.DeploymentLicensedAgents.Should().Be(47);
        day.DeploymentRowHash.Should().Be("lac1:deployment-20260917-1");
        day.Tenants.Select(t => (t.TenantId, t.LicensedAgents, t.Revision)).Should().Equal(("c1", 32, 1), ("c2", 15, 0));
    }

    [Fact]
    public void BuildPeaks_ShouldPickTheEarliestDayOfTheHighestTotal()
    {
        var response = Peaks([
            Daily(null, new DateOnly(2026, 9, 3), 12), Daily(null, new DateOnly(2026, 9, 9), 20),
            Daily(null, new DateOnly(2026, 9, 2), 20), Daily(null, new DateOnly(2026, 9, 5), 7),
        ]);

        response.Deployment.PeakLicensedAgents.Should().Be(20);
        response.Deployment.PeakDay.Should().Be(new DateOnly(2026, 9, 2));
        response.Deployment.OverBand.Should().BeFalse();
        response.Days.Select(d => d.Day.Day).Should().Equal(2, 3, 5, 9);
    }

    [Fact]
    public void BuildPeaks_ShouldListOnlyDaysWhoseDeploymentTotalIsClosed_AndOnlyDaysInTheRange()
    {
        var response = Peaks([
            Daily("c1", new DateOnly(2026, 9, 29), 4),                 // the deployment total waits for another chain
            Daily(null, new DateOnly(2026, 8, 31), 99), Daily(null, new DateOnly(2026, 10, 1), 98),
            Daily(null, new DateOnly(2026, 9, 28), 3), Daily("c1", new DateOnly(2026, 9, 28), 3),
        ]);

        response.Days.Select(d => d.Day).Should().Equal(new DateOnly(2026, 9, 28));
        response.Deployment.PeakLicensedAgents.Should().Be(3);
    }

    [Fact]
    public void BuildPeaks_ShouldHaveNoPeakDay_WhenNoDayIsClosed()
    {
        var response = Peaks([], new LicensedAgentLicenseDto("lic", "Owner", "Developer", 5, true));

        response.Deployment.PeakLicensedAgents.Should().Be(0);
        response.Deployment.PeakDay.Should().BeNull();
        response.Deployment.OverBand.Should().BeFalse();
        response.Deployment.PartnerSubtotals.Should().BeEmpty();
        response.Days.Should().BeEmpty();
    }

    [Fact]
    public void BuildPeaks_ShouldComputeOverBandServerSide_FromTheLicenceBand()
    {
        var rows = new[] { Daily(null, new DateOnly(2026, 9, 10), 63) };

        Peaks(rows, new LicensedAgentLicenseDto("lic", "Owner", "SelfHostBusiness", 50, true)).Deployment.OverBand.Should().BeTrue();
        Peaks(rows, new LicensedAgentLicenseDto("lic", "Owner", "SaaSBusiness", 0, true)).Deployment.OverBand.Should().BeFalse();
        Peaks(rows, Undeclared).Deployment.OverBand.Should().BeFalse();
    }

    [Fact]
    public void BuildPeaks_ShouldNameEachTenant_AndFallBackToItsIdWhenTheTenantIsGone()
    {
        var d = new DateOnly(2026, 9, 4);
        var response = Peaks(
            [Daily(null, d, 3), Daily("c1", d, 2), Daily("gone", d, 1)],
            tenants: new Dictionary<string, LicensedAgentTenantRef> { ["c1"] = Ref("Acme Support", "Customer", "platform") });

        response.Days.Single().Tenants.Select(t => t.TenantName).Should().Equal("Acme Support", "gone");
        response.Days.Single().Tenants.Select(t => t.ParentTenantId).Should().Equal("platform", null);
    }

    [Fact]
    public void BuildExport_ShouldOrderRowsByChain_ThenBySequence()
    {
        LicenseAgentEvent Event(string? tenantId, long sequence) => new()
        {
            TenantId = tenantId,
            Sequence = sequence,
            EventId = Guid.NewGuid(),
            Kind = LicenseAgentEventKinds.ChainAnchored,
            OccurredAt = DateTimeOffset.UnixEpoch,
            AgentId = null,
            UserId = null,
            ActorUserId = null,
            ConversationId = null,
            UserStatus = null,
            Counted = null,
            LicenseId = null,
            PrevHash = "",
            RowHash = "",
        };

        var export = LicensedAgentReports.BuildExport(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            new LicensedAgentExportData("UTC",
                [Event(null, 2), Event("c2", 1), Event("c1", 2), Event(null, 1), Event("c1", 1)],
                [],
                [new LicenseAgentChainHead(null, 2, "h", null), new LicenseAgentChainHead("c2", 1, "h", null), new LicenseAgentChainHead("c1", 2, "h", null)],
                new Dictionary<string, LicensedAgentTenantRef>()),
            Undeclared,
            DateTimeOffset.UnixEpoch);

        export.Events.Select(e => (e.TenantId, e.Sequence)).Should().Equal(("c1", 1L), ("c1", 2L), ("c2", 1L), (null, 1L), (null, 2L));
        export.ChainHeads.Select(h => h.TenantId).Should().Equal("c1", "c2", null);
        export.HashScheme.Should().Be("lac1");
        export.SchemaVersion.Should().Be(1);
    }

    // ─── partner-channel-pilot-fixes P5 (licensed-agent-reporting, design D8/D9) ──────

    private static readonly Dictionary<string, LicensedAgentTenantRef> Hierarchy = new(StringComparer.Ordinal)
    {
        ["platform"] = Ref("Verbara", "Platform", null),
        ["p1"] = Ref("Reseller One", "Partner", "platform"),
        ["pc1"] = Ref("Partner Customer 1", "Customer", "p1"),
        ["pc2"] = Ref("Partner Customer 2", "Customer", "p1"),
        ["dc1"] = Ref("Direct Customer", "Customer", "platform"),
    };

    [Fact]
    public void BuildPeaks_ShouldSubtotalPartnersOnPeakDay_NotPerTenantPeaks()
    {
        var d1 = new DateOnly(2026, 9, 1);
        var d2 = new DateOnly(2026, 9, 2);
        var response = Peaks(
            [
                Daily(null, d1, 42), Daily("pc1", d1, 10), Daily("pc2", d1, 2), Daily("dc1", d1, 30),
                Daily(null, d2, 47), Daily("pc1", d2, 3), Daily("pc2", d2, 9), Daily("dc1", d2, 35),
            ],
            tenants: Hierarchy);

        response.Deployment.PeakDay.Should().Be(d2);
        response.Deployment.PartnerSubtotals.Should().Equal(new LicensedAgentPartnerSubtotalDto("p1", "Reseller One", 12));
        response.Days[^1].Tenants.Single(t => t.TenantId == "dc1").ParentTenantId.Should().Be("platform",
            "a direct Customer names the Platform tenant and contributes to no subtotal");
    }

    [Fact]
    public void BuildPeaks_ShouldReturnEmptyPartnerSubtotals_WhenNoDayClosed()
    {
        Peaks([Daily("pc1", new DateOnly(2026, 9, 1), 4)], tenants: Hierarchy).Deployment.PartnerSubtotals.Should().BeEmpty();
    }

    [Fact]
    public void BuildPeaks_ShouldReturnEmptyPartnerSubtotals_WhenOnlyDirectCustomersHaveRowsOnThePeakDay()
    {
        var d = new DateOnly(2026, 9, 3);
        Peaks([Daily(null, d, 5), Daily("dc1", d, 5)], tenants: Hierarchy).Deployment.PartnerSubtotals.Should().BeEmpty();
    }

    [Fact]
    public void BuildPeaks_ShouldKeepEveryRowHash_WhenParentTenantIdAdded()
    {
        var d = new DateOnly(2026, 9, 4);
        var rows = new[] { Daily(null, d, 13), Daily("pc1", d, 3), Daily("dc1", d, 10) };

        var without = Peaks(rows);
        var with = Peaks(rows, tenants: Hierarchy);

        with.Days.Select(x => x.DeploymentRowHash).Should().Equal(without.Days.Select(x => x.DeploymentRowHash));
        with.Days.SelectMany(x => x.Tenants).Select(t => (t.TenantId, t.RowHash))
            .Should().Equal(without.Days.SelectMany(x => x.Tenants).Select(t => (t.TenantId, t.RowHash)));
        with.Days.Single().Tenants.Select(t => t.ParentTenantId).Should().Equal("platform", "p1");
    }

    [Fact]
    public void BuildExport_ShouldCloseTenantMapOverParents()
    {
        var export = LicensedAgentReports.BuildExport(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            new LicensedAgentExportData("UTC", [], [],
                [new LicenseAgentChainHead("pc1", 1, "h", null), new LicenseAgentChainHead("dc1", 1, "h", null),
                 new LicenseAgentChainHead("gone", 1, "h", null), new LicenseAgentChainHead(null, 1, "h", null)],
                Hierarchy),
            Undeclared,
            DateTimeOffset.UnixEpoch);

        export.Tenants.Should().Equal(
            new LicensedAgentExportTenantDto("dc1", "Customer", "platform"),
            new LicensedAgentExportTenantDto("gone", null, null),
            new LicensedAgentExportTenantDto("p1", "Partner", "platform"),
            new LicensedAgentExportTenantDto("pc1", "Customer", "p1"),
            new LicensedAgentExportTenantDto("platform", "Platform", null));
    }
}
