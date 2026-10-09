using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Queues.Tests.Licensing;

/// <summary>
/// licensed-agent-metering (tasks.md 4.3; licensed-agent-daily-close): the pure daily peak, its start set,
/// and the deterministic scripted month the spec's first scenario names.
/// </summary>
public sealed class LicensedAgentPeakTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void LicensedAgentPeak_ShouldMatchExpectedPeaks_ForScriptedMonth()
    {
        // One Customer tenant, October 2026, day zone UTC. Every row is stamped with the injected clock.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var chain = LicenseAgentChainBuilder.Anchor("ten-month", "lic-1", clock.GetUtcNow());

        void At(int day, int hour, int minute = 0) =>
            clock.SetUtcNow(new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.Zero));
        void Row(string kind, string agent, bool counted, string? status = null, string? conversation = null) =>
            chain.Append(kind, clock.GetUtcNow(), agent, "u" + agent[1..], counted, status, conversation);

        // Day 1 — upgrade baseline: a1..a3 counted; a4's user is Suspended.
        Row(LicenseAgentEventKinds.AgentBaseline, "a1", true);
        Row(LicenseAgentEventKinds.AgentBaseline, "a2", true);
        Row(LicenseAgentEventKinds.AgentBaseline, "a3", true);
        Row(LicenseAgentEventKinds.AgentBaseline, "a4", false);
        // Day 2 — a creation.
        At(2, 9); Row(LicenseAgentEventKinds.AgentCreated, "a5", true);
        // Day 3 — created and deleted within one day: still counts at its peak.
        At(3, 10); Row(LicenseAgentEventKinds.AgentCreated, "a6", true);
        At(3, 11); Row(LicenseAgentEventKinds.AgentDeleted, "a6", false);
        // Day 4 — a suspension; Day 5 — the reactivation.
        At(4, 8); Row(LicenseAgentEventKinds.UserStatusChanged, "a1", false, "Suspended");
        At(5, 14); Row(LicenseAgentEventKinds.UserStatusChanged, "a1", true, "Active");
        // Day 6 — a takeover by an agent already counted adds nothing.
        At(6, 10); Row(LicenseAgentEventKinds.ConversationTakenOver, "a2", true, conversation: "c1");
        // Day 7 — a4 reactivated, then receives a transfer (already counted: no double count).
        At(7, 12); Row(LicenseAgentEventKinds.UserStatusChanged, "a4", true, "Active");
        At(7, 13); Row(LicenseAgentEventKinds.ConversationTransferred, "a4", true, conversation: "c2");
        // Day 8 — a4 suspended again, then a supervisor reassigns c2 to a5.
        At(8, 9); Row(LicenseAgentEventKinds.UserStatusChanged, "a4", false, "Suspended");
        At(8, 10); Row(LicenseAgentEventKinds.ConversationReassigned, "a5", true, conversation: "c2");
        // Day 9 — a deletion.
        At(9, 1); Row(LicenseAgentEventKinds.AgentDeleted, "a5", false);
        // Day 15 — three creations, one deleted the same evening.
        At(15, 10); Row(LicenseAgentEventKinds.AgentCreated, "a7", true);
        At(15, 10, 1); Row(LicenseAgentEventKinds.AgentCreated, "a8", true);
        At(15, 10, 2); Row(LicenseAgentEventKinds.AgentCreated, "a9", true);
        At(15, 18); Row(LicenseAgentEventKinds.AgentDeleted, "a7", false);
        // Day 20 — suspended, then deactivated: the second change does not move the count.
        At(20, 5); Row(LicenseAgentEventKinds.UserStatusChanged, "a2", false, "Suspended");
        At(20, 6); Row(LicenseAgentEventKinds.UserStatusChanged, "a2", false, "Deactivated");

        int[] expected =
        [
            3, 4, 5, 4, 4, 4, 5, 5, 4, 3, // 1-10
            3, 3, 3, 3, 6, 5, 5, 5, 5, 5, // 11-20
            4, 4, 4, 4, 4, 4, 4, 4, 4, 4, // 21-30
            4,                            // 31
        ];

        var peaks = Enumerable.Range(1, 31)
            .Select(day => LicensedAgentPeak.ComputeDay(chain.Rows, new DateOnly(2026, 10, day), Utc))
            .ToArray();

        peaks.Should().Equal(expected);
        peaks.Max().Should().Be(6, "the month figure is the highest daily peak");
    }

    [Fact]
    public void Compute_ShouldCountAnAgentCreatedAndDeletedWithinTheDay_AtItsPeak()
    {
        var start = Enumerable.Range(1, 10).Select(i => $"a{i}").ToHashSet();
        var chain = LicenseAgentChainBuilder.Anchor("t", null, Hour(0));
        chain.Append(LicenseAgentEventKinds.AgentCreated, Hour(10), "a11", "u11", counted: true);
        chain.Append(LicenseAgentEventKinds.AgentDeleted, Hour(11), "a11", "u11", counted: false);

        LicensedAgentPeak.Compute(start, chain.Rows.Skip(1)).Should().Be(11);
    }

    [Fact]
    public void Compute_ShouldAddAnOwnershipReceiverOnlyOnce_AndNeverRemoveOne()
    {
        var start = new HashSet<string> { "a1" };
        var chain = LicenseAgentChainBuilder.Anchor("t", null, Hour(0));
        chain.Append(LicenseAgentEventKinds.ConversationTakenOver, Hour(1), "a1", "u1", counted: true);
        chain.Append(LicenseAgentEventKinds.ConversationReassigned, Hour(2), "a1", "u1", counted: false);
        chain.Append(LicenseAgentEventKinds.ConversationTransferred, Hour(3), "a2", "u2", counted: true);
        chain.Append(LicenseAgentEventKinds.ConversationReassigned, Hour(4), "a2", "u2", counted: true);

        LicensedAgentPeak.Compute(start, chain.Rows.Skip(1)).Should().Be(2,
            "a2 is added once; an ownership row whose receiver is not counted removes no one");
    }

    [Fact]
    public void StartSet_ShouldTakeEachAgentsLastRow_InSequenceOrder()
    {
        var chain = LicenseAgentChainBuilder.Anchor("t", null, Hour(0));
        chain.Append(LicenseAgentEventKinds.AgentBaseline, Hour(0), "a1", "u1", counted: true);
        chain.Append(LicenseAgentEventKinds.AgentBaseline, Hour(0), "a2", "u2", counted: true);
        chain.Append(LicenseAgentEventKinds.UserStatusChanged, Hour(1), "a2", "u2", counted: false, userStatus: "Suspended");
        chain.Reanchor("lic-2", Hour(2));

        LicensedAgentPeak.StartSet(chain.Rows).Should().BeEquivalentTo(["a1"]);
    }

    [Theory]
    [InlineData("America/New_York", 2026, 3, 8, 23)]
    [InlineData("America/New_York", 2026, 11, 1, 25)]
    [InlineData("Europe/Madrid", 2026, 3, 29, 23)]
    [InlineData("Europe/Madrid", 2026, 10, 25, 25)]
    [InlineData("UTC", 2026, 3, 8, 24)]
    [InlineData("America/Bogota", 2026, 3, 8, 24)]
    public void Bounds_ShouldCloseDaylightSavingDaysWhole_From23To25Hours(string zone, int y, int m, int d, int hours)
    {
        var tz = LicenseAgentDayZone.Resolve(zone);

        var (start, end) = LicenseAgentDayZone.Bounds(new DateOnly(y, m, d), tz);

        (end - start).Should().Be(TimeSpan.FromHours(hours));
    }

    [Fact]
    public void DayOf_ShouldAttributeTheRepeatedLocalHour_ToTheDayThatContainsItsUtcInstant()
    {
        var tz = LicenseAgentDayZone.Resolve("America/New_York");
        // 2026-11-01 01:30 local happens twice: 05:30Z (EDT) and 06:30Z (EST). Both are on Nov 1.
        LicenseAgentDayZone.DayOf(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), tz).Should().Be(new DateOnly(2026, 11, 1));
        LicenseAgentDayZone.DayOf(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), tz).Should().Be(new DateOnly(2026, 11, 1));
        // The skipped hour: 2026-03-08 07:30Z is 03:30 EDT on Mar 8; 04:59Z is 23:59 EST on Mar 7.
        LicenseAgentDayZone.DayOf(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), tz).Should().Be(new DateOnly(2026, 3, 8));
        LicenseAgentDayZone.DayOf(new DateTimeOffset(2026, 3, 8, 4, 59, 0, TimeSpan.Zero), tz).Should().Be(new DateOnly(2026, 3, 7));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/New_York")]
    [InlineData("America/Havana")] // its clocks move at local midnight: some midnights do not exist
    [InlineData("America/Santiago")]
    [InlineData("Asia/Kolkata")]
    public void Bounds_ShouldTileEveryDayOfTheYear_WithoutGapOrOverlap(string zone)
    {
        var tz = LicenseAgentDayZone.Resolve(zone);
        for (var day = new DateOnly(2026, 1, 1); day < new DateOnly(2027, 1, 1); day = day.AddDays(1))
        {
            var (start, end) = LicenseAgentDayZone.Bounds(day, tz);
            LicenseAgentDayZone.Bounds(day.AddDays(1), tz).Start.Should().Be(end, $"{zone} {day:yyyy-MM-dd} ends where the next day starts");
            (end - start).TotalHours.Should().BeOneOf(23d, 24d, 25d);
            LicenseAgentDayZone.DayOf(start, tz).Should().Be(day);
            LicenseAgentDayZone.DayOf(end.AddTicks(-10), tz).Should().Be(day);
        }
    }

    [Fact]
    public void Resolve_ShouldRejectAnUnknownZone_NamingIt()
    {
        var act = () => LicenseAgentDayZone.Resolve("Mars/Olympus_Mons");

        act.Should().Throw<ArgumentException>().WithMessage("*Mars/Olympus_Mons*");
    }

    private static DateTimeOffset Hour(int h) => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddHours(h);
}
