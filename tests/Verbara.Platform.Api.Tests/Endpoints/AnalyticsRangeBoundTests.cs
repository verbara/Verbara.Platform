using FluentAssertions;
using Verbara.Platform.Api.Endpoints.Shared;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

public sealed class AnalyticsRangeBoundTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDayStart = DayStart.AddDays(1);

    [Fact]
    public void ParseFrom_ShouldStartAtUtcMidnight_WhenValueIsDateOnly()
    {
        var bound = AnalyticsRangeBound.ParseFrom("2026-03-04");

        bound.Should().Be(DayStart);
        bound!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ParseTo_ShouldEndJustBeforeTheNextUtcDay_WhenValueIsDateOnly()
    {
        var bound = AnalyticsRangeBound.ParseTo("2026-03-04");

        bound.Should().Be(NextDayStart.AddTicks(-TimeSpan.TicksPerMicrosecond));
        bound!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ParseTo_ShouldKeepTheInstant_WhenValueCarriesATime()
    {
        AnalyticsRangeBound.ParseTo("2026-03-04T12:00:00Z").Should().Be(DayStart.AddHours(12));
    }

    [Fact]
    public void ParseTo_ShouldNormaliseToUtc_WhenValueCarriesAnOffset()
    {
        var bound = AnalyticsRangeBound.ParseTo("2026-03-04T07:00:00-05:00");

        bound.Should().Be(DayStart.AddHours(12));
        bound!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ParseFromAndParseTo_ShouldReturnNull_WhenValueIsAbsent()
    {
        AnalyticsRangeBound.ParseFrom(null).Should().BeNull();
        AnalyticsRangeBound.ParseTo(null).Should().BeNull();
    }

    [Fact]
    public void ParseTo_ShouldThrowFormatException_WhenValueIsMalformed()
    {
        var act = () => AnalyticsRangeBound.ParseTo("not-a-date");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseTo_ShouldNotOverflow_WhenValueIsTheLastRepresentableDay()
    {
        AnalyticsRangeBound.ParseTo("9999-12-31").Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void TryParseTo_ShouldCoverTheWholeDay_WhenValueIsDateOnly()
    {
        AnalyticsRangeBound.TryParseTo("2026-03-04", out var bound).Should().BeTrue();

        bound.Should().Be(NextDayStart.AddTicks(-TimeSpan.TicksPerMicrosecond));
    }

    [Fact]
    public void TryParseFrom_ShouldStartAtUtcMidnight_WhenValueIsDateOnly()
    {
        AnalyticsRangeBound.TryParseFrom("2026-03-04", out var bound).Should().BeTrue();

        bound.Should().Be(DayStart);
    }

    [Fact]
    public void TryParseFromAndTryParseTo_ShouldKeepTheInstant_WhenValueCarriesATime()
    {
        AnalyticsRangeBound.TryParseFrom("2026-03-04T06:00:00Z", out var from).Should().BeTrue();
        AnalyticsRangeBound.TryParseTo("2026-03-04T12:00:00+00:00", out var to).Should().BeTrue();

        from.Should().Be(DayStart.AddHours(6));
        to.Should().Be(DayStart.AddHours(12));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void TryParseFromAndTryParseTo_ShouldReturnFalse_WhenValueIsAbsentOrMalformed(string? value)
    {
        AnalyticsRangeBound.TryParseFrom(value, out _).Should().BeFalse();
        AnalyticsRangeBound.TryParseTo(value, out _).Should().BeFalse();
    }
}
