using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Queues.Tests.Licensing;

/// <summary>
/// licensed-agent-metering slice 3 (tasks.md 7.3; licensed-agent-reporting): which rows of one chain the
/// export carries for a range — a contiguous run that starts at the row immediately before the range and
/// ends at the last row written in it (or the last revision of a day inside it).
/// </summary>
public sealed class LicensedAgentExportSpanTests
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);
    private static readonly DateOnly Sep30 = new(2026, 9, 30);
    private static readonly (DateTimeOffset Start, DateTimeOffset End) September =
        LicensedAgentExportSpan.Bounds(Sep1, Sep30, TimeZoneInfo.Utc);

    private static DateTimeOffset At(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    private static LicenseAgentEvent Event(long sequence, DateTimeOffset at) => new()
    {
        TenantId = "c1",
        Sequence = sequence,
        EventId = Guid.NewGuid(),
        Kind = LicenseAgentEventKinds.AgentCreated,
        OccurredAt = at,
        AgentId = "a",
        UserId = "u",
        ActorUserId = null,
        ConversationId = null,
        UserStatus = null,
        Counted = true,
        LicenseId = null,
        PrevHash = "",
        RowHash = "",
    };

    private static LicenseAgentDaily Daily(long sequence, DateOnly day, DateTimeOffset closedAt, int revision = 0) => new()
    {
        TenantId = "c1",
        Sequence = sequence,
        Day = day,
        Revision = revision,
        LicensedAgents = 1,
        ClosedAt = closedAt,
        ClosedThroughSequence = sequence - 1,
        LicenseId = null,
        PrevHash = "",
        RowHash = "",
    };

    private static (long, long)? Span(IEnumerable<LicenseAgentEvent> events, IEnumerable<LicenseAgentDaily> daily, long head) =>
        LicensedAgentExportSpan.Range(events, daily, head, September.Start, September.End, Sep1, Sep30);

    [Fact]
    public void Range_ShouldStartAtThePredecessorRow_AndEndAtTheLastRowOfTheRange()
    {
        var events = new[] { Event(1, At(8, 1)), Event(2, At(8, 20)), Event(3, At(9, 3)), Event(4, At(9, 29)), Event(6, At(10, 2)) };
        var daily = new[] { Daily(5, new DateOnly(2026, 9, 29), At(9, 30, 0)) };

        Span(events, daily, head: 6).Should().Be((2L, 5L),
            "row 2 is the one immediately before the range, and row 6 was written after it ended");
    }

    [Fact]
    public void Range_ShouldReachALateCorrectionOfADayInTheRange_AndEveryRowBeforeIt()
    {
        var events = new[] { Event(1, At(9, 10)), Event(3, At(10, 1)) };
        var daily = new[]
        {
            Daily(2, new DateOnly(2026, 9, 30), At(10, 1, 0)),
            Daily(4, new DateOnly(2026, 9, 30), At(10, 3), revision: 1),
        };

        Span(events, daily, head: 4).Should().Be((1L, 4L), "every revision of an exported day is exported, and the chain stays contiguous");
    }

    [Fact]
    public void Range_ShouldEndAtTheHead_WhenTheRangeRunsToThePresent()
    {
        var events = new[] { Event(1, At(9, 2)), Event(2, At(9, 7)), Event(3, At(9, 8)) };

        Span(events, [], head: 3).Should().Be((1L, 3L));
    }

    [Fact]
    public void Range_ShouldBeTheHeadRowAlone_WhenTheChainHasNotChangedSinceBeforeTheRange()
    {
        var events = new[] { Event(1, At(7, 1)), Event(2, At(8, 1)) };

        Span(events, [], head: 2).Should().Be((2L, 2L), "the row before the range is the chain's head, which the verifier matches");
    }

    [Fact]
    public void Range_ShouldBeEmpty_WhenTheChainWasAnchoredAfterTheRange()
    {
        var events = new[] { Event(1, At(10, 5)), Event(2, At(10, 6)) };

        Span(events, [], head: 2).Should().BeNull();
    }

    [Fact]
    public void Range_ShouldStartAtTheFirstRow_WhenTheChainWasAnchoredInsideTheRange()
    {
        var events = new[] { Event(1, At(9, 5)), Event(2, At(9, 6)) };

        Span(events, [], head: 2).Should().Be((1L, 2L));
    }

    [Theory]
    [InlineData(null, null, 9L, null)]
    [InlineData(null, 9L, 9L, "9-9")]
    [InlineData(1L, 4L, 9L, "1-4")]
    [InlineData(5L, 4L, 9L, "4-4")]
    [InlineData(7L, 4L, 9L, null)]
    public void Range_ShouldApplyTheSameRule_FromItsTwoMeasurements(long? firstSince, long? lastBefore, long head, string? expected)
    {
        var span = LicensedAgentExportSpan.Range(firstSince, lastBefore, head);

        (span is { } s ? $"{s.First}-{s.Last}" : null).Should().Be(expected);
    }

    [Fact]
    public void Bounds_ShouldCutTheRangeAtLocalMidnights_InTheDayZone()
    {
        var bogota = LicenseAgentDayZone.Resolve("America/Bogota");

        var (start, end) = LicensedAgentExportSpan.Bounds(Sep1, Sep30, bogota);

        start.Should().Be(new DateTimeOffset(2026, 9, 1, 5, 0, 0, TimeSpan.Zero));
        end.Should().Be(new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CompareChains_ShouldOrderTenantChainsOrdinally_ThenTheDeploymentChain()
    {
        var keys = new List<string?> { null, "b", "B", "a" };

        keys.Sort(LicensedAgentExportSpan.CompareChains);

        keys.Should().Equal("B", "a", "b", null);
    }
}
