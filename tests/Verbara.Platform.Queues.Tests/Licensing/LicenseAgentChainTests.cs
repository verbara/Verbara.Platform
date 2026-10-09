using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Queues.Tests.Licensing;

/// <summary>
/// licensed-agent-metering (tasks.md 4.2; licensed-agent-ledger, design D6): the <c>lac1</c> chain primitives.
/// The golden values were computed outside .NET (Python <c>hashlib</c>, from the canonical form design D6
/// states) over the field values of <c>fixtures/licensed-agent-export.v1.json</c>. The fixture's own hashes are
/// illustrative (they are not linked into a chain and recompute under no canonical form), so the vectors
/// check the fixture rows' structure and the canonical form, not the fixture's hash strings.
/// </summary>
public sealed class LicenseAgentChainTests
{
    private static LicenseAgentEvent FixtureEvent180() => new()
    {
        TenantId = "ten-acme",
        Sequence = 180,
        EventId = Guid.Parse("8b1c0e7a-4d55-4a51-9a1e-1f7d2b3c4d01"),
        Kind = LicenseAgentEventKinds.AgentCreated,
        OccurredAt = DateTimeOffset.Parse("2026-09-17T13:02:44Z", System.Globalization.CultureInfo.InvariantCulture),
        AgentId = "agt-0031",
        UserId = "usr-0031",
        ActorUserId = "usr-admin-01",
        ConversationId = null,
        UserStatus = null,
        Counted = true,
        LicenseId = "lic-7f3c2a91",
        PrevHash = "lac1:2310dc42956bcc4d4ea8d254319d7101fb0b0ef03e0f11160c4c099296458370",
        RowHash = "",
    };

    [Fact]
    public void LicenseAgentChain_ShouldMatchGoldenVector_ForFixtureLedgerRow()
    {
        var row = FixtureEvent180();

        LicenseAgentChain.CanonicalEvent(row).Should().Be(
            "lac1:2310dc42956bcc4d4ea8d254319d7101fb0b0ef03e0f11160c4c099296458370|ten-acme|180|" +
            "8b1c0e7a-4d55-4a51-9a1e-1f7d2b3c4d01|agent_created|2026-09-17T13:02:44.0000000Z|agt-0031|usr-0031|" +
            "usr-admin-01|||true|lic-7f3c2a91");
        LicenseAgentChain.RowHash(row).Should().Be(
            "lac1:41511d95cdb46caca63a8759549f6d611cc04e0e41a863ee67bb1ff9b45ad114");
    }

    [Fact]
    public void LicenseAgentChain_ShouldMatchGoldenVector_ForStatusAndReanchorRows()
    {
        var suspended = FixtureEvent180() with
        {
            Sequence = 181,
            EventId = Guid.Parse("8b1c0e7a-4d55-4a51-9a1e-1f7d2b3c4d02"),
            Kind = LicenseAgentEventKinds.UserStatusChanged,
            OccurredAt = new DateTimeOffset(2026, 9, 17, 15, 20, 0, TimeSpan.Zero),
            AgentId = "agt-0012",
            UserId = "usr-0012",
            UserStatus = "Suspended",
            Counted = false,
            PrevHash = "lac1:09e42d2c2e9b55c970fe8b6fdc83fa3dfe2060de4ee544fdf2a87cd5aa4a2d2f",
        };
        var reanchored = FixtureEvent180() with
        {
            Sequence = 190,
            EventId = Guid.Parse("8b1c0e7a-4d55-4a51-9a1e-1f7d2b3c4d04"),
            Kind = LicenseAgentEventKinds.ChainReanchored,
            OccurredAt = new DateTimeOffset(2026, 9, 20, 0, 0, 3, TimeSpan.Zero),
            AgentId = null,
            UserId = null,
            ActorUserId = null,
            Counted = null,
            LicenseId = "lic-9a04be22",
            PrevHash = "lac1:58572ea4567fa6cf88ee9b96aff71700572f90ffb202a99b7f8883916890d193",
        };

        LicenseAgentChain.RowHash(suspended).Should().Be(
            "lac1:b1afa0b3717c5fa1df3cda3d83c6d6f0d86a67160c6188b46a5a8f0c9af4b6c1");
        LicenseAgentChain.RowHash(reanchored).Should().Be(
            "lac1:0c8aa740c829461066515ca6c7834e36aac6a56b17f13c5e3c4e86420a0266f7");
    }

    [Fact]
    public void LicenseAgentChain_ShouldMatchGoldenVector_ForTenantAndDeploymentDailyRows()
    {
        var tenantDay = new LicenseAgentDaily
        {
            TenantId = "ten-acme",
            Sequence = 185,
            Day = new DateOnly(2026, 9, 17),
            Revision = 0,
            LicensedAgents = 31,
            ClosedAt = new DateTimeOffset(2026, 9, 18, 0, 5, 9, TimeSpan.Zero),
            ClosedThroughSequence = 184,
            LicenseId = "lic-7f3c2a91",
            PrevHash = "lac1:ee251e14ed79a30102169d29e562165fdbecc29f7a27c2aa96d1b610ef3202a6",
            RowHash = "",
        };
        var deploymentDay = tenantDay with
        {
            TenantId = null,
            Sequence = 24,
            Revision = 1,
            LicensedAgents = 47,
            ClosedAt = new DateTimeOffset(2026, 9, 18, 9, 41, 3, TimeSpan.Zero),
            ClosedThroughSequence = 23,
            PrevHash = "lac1:64ee07e65c2267154368a9fa953ff28341a2f3a92454ada732162ed21bc19d79",
        };

        LicenseAgentChain.CanonicalDaily(tenantDay).Should().Be(
            "lac1:ee251e14ed79a30102169d29e562165fdbecc29f7a27c2aa96d1b610ef3202a6|ten-acme|185|2026-09-17|0|31|" +
            "2026-09-18T00:05:09.0000000Z|184|lic-7f3c2a91");
        LicenseAgentChain.RowHash(tenantDay).Should().Be(
            "lac1:b3c900330f40225133f22e1a818a49454d307aff2a02a03684faa0e555ca9889");
        LicenseAgentChain.RowHash(deploymentDay).Should().Be(
            "lac1:1b70941b0a8bac9ba3303ca82e2eb7de80335df1a2000cda710dcb582b1484af",
            "the deployment chain renders its null tenantId as the empty string");
    }

    [Fact]
    public void Genesis_ShouldBindTenantAndLicence_AndUseTheNullFormWhenEitherIsMissing()
    {
        LicenseAgentChain.Genesis("ten-acme", "lic-7f3c2a91").Should().Be(
            "lac1:1624cf86f17f35663c7de384e92f58ef911f46ea91b154051f1f672a7e559176");
        LicenseAgentChain.Genesis(null, null).Should().Be(
            "lac1:524eebe304084efc76a86850e8fdb9f2821b21e62b9ad357709aa5d9c05b3d7b",
            "the deployment chain is named 'deployment' and a missing licence renders as the empty string");
        LicenseAgentChain.Genesis("ten-acme", null).Should().Be(
            "lac1:cbe47fa49efff59ab6e3ab8fdbf8ba7e8ad6bc36c86ca5ea6aaa9295f45e829a");
    }

    [Fact]
    public void RowHash_ShouldChange_WhenAnyCanonicalFieldIsTampered()
    {
        var row = FixtureEvent180();
        var original = LicenseAgentChain.RowHash(row);

        LicenseAgentChain.RowHash(row with { Counted = false }).Should().NotBe(original);
        LicenseAgentChain.RowHash(row with { PrevHash = LicenseAgentChain.Genesis("ten-acme", null) }).Should().NotBe(original);
        LicenseAgentChain.RowHash(row with { OccurredAt = row.OccurredAt.AddTicks(10) }).Should().NotBe(original);
        LicenseAgentChain.RowHash(row with { LicenseId = null }).Should().NotBe(original);
    }

    [Fact]
    public void Normalize_ShouldTruncateToMicroseconds_SoAPostgresRoundTripKeepsTheHash()
    {
        var precise = new DateTimeOffset(2026, 9, 17, 13, 2, 44, TimeSpan.FromHours(-5)).AddTicks(1234567);

        var normalized = LicenseAgentChain.Normalize(precise);

        normalized.Offset.Should().Be(TimeSpan.Zero);
        normalized.UtcTicks.Should().Be(precise.UtcTicks - 7, "Postgres timestamptz keeps microseconds (10 ticks)");
        LicenseAgentChain.FormatInstant(normalized).Should().Be("2026-09-17T18:02:44.1234560Z");
    }

    [Fact]
    public void Verify_ShouldReportTheTamperedSequence_WhenARowWasAlteredAfterItWasWritten()
    {
        var chain = LicenseAgentChainBuilder.Anchor("ten-acme", "lic-1", At(0));
        chain.Append(LicenseAgentEventKinds.AgentCreated, At(1), "a1", "u1", counted: true);
        chain.Append(LicenseAgentEventKinds.AgentCreated, At(2), "a2", "u2", counted: true);
        chain.Append(LicenseAgentEventKinds.UserStatusChanged, At(3), "a1", "u1", counted: false, userStatus: "Suspended");
        var rows = chain.Rows.ToList();

        LicenseAgentChain.Verify("ten-acme", "lic-1", rows, []).Should().BeNull();

        rows[2] = rows[2] with { Counted = false };
        LicenseAgentChain.Verify("ten-acme", "lic-1", rows, []).Should().Be(3, "the third row (sequence 3) no longer hashes to its rowHash");
    }

    [Fact]
    public void Reanchor_ShouldLinkToTheOldHead_AndNeverStartANewGenesis()
    {
        var chain = LicenseAgentChainBuilder.Anchor("ten-acme", "lic-1", At(0));
        chain.Append(LicenseAgentEventKinds.AgentCreated, At(1), "a1", "u1", counted: true);
        var oldHead = chain.Rows[^1].RowHash;

        chain.Reanchor("lic-2", At(2));

        var reanchor = chain.Rows[^1];
        reanchor.Kind.Should().Be(LicenseAgentEventKinds.ChainReanchored);
        reanchor.PrevHash.Should().Be(oldHead);
        reanchor.LicenseId.Should().Be("lic-2");
        chain.Rows[0].PrevHash.Should().Be(LicenseAgentChain.Genesis("ten-acme", "lic-1"));
        LicenseAgentChain.NeedsReanchor("lic-1", "lic-2").Should().BeTrue();
        LicenseAgentChain.NeedsReanchor(null, "lic-2").Should().BeTrue();
        LicenseAgentChain.NeedsReanchor("lic-2", null).Should().BeTrue("losing the licence is a licence change too");
        LicenseAgentChain.NeedsReanchor(null, null).Should().BeFalse();
        LicenseAgentChain.NeedsReanchor("lic-2", "lic-2").Should().BeFalse();
        LicenseAgentChain.Verify("ten-acme", "lic-1", chain.Rows, []).Should().BeNull();
    }

    private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
}
