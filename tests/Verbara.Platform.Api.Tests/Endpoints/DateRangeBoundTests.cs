using FluentAssertions;
using Verbara.Platform.Api.Endpoints.Shared;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

public sealed class DateRangeBoundTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDayStart = DayStart.AddDays(1);

    [Fact]
    public void ParseFrom_ShouldStartAtUtcMidnight_WhenValueIsDateOnly()
    {
        var bound = DateRangeBound.ParseFrom("2026-03-04");

        bound.Should().Be(DayStart);
        bound!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ParseTo_ShouldEndJustBeforeTheNextUtcDay_WhenValueIsDateOnly()
    {
        var bound = DateRangeBound.ParseTo("2026-03-04");

        bound.Should().Be(NextDayStart.AddTicks(-TimeSpan.TicksPerMicrosecond));
        bound!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ParseTo_ShouldKeepTheInstant_WhenValueCarriesATime()
    {
        DateRangeBound.ParseTo("2026-03-04T12:00:00Z").Should().Be(DayStart.AddHours(12));
    }

    [Fact]
    public void ParseTo_ShouldNormaliseToUtc_WhenValueCarriesAnOffset()
    {
        var bound = DateRangeBound.ParseTo("2026-03-04T07:00:00-05:00");

        bound.Should().Be(DayStart.AddHours(12));
        bound!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ParseFromAndParseTo_ShouldReturnNull_WhenValueIsAbsent()
    {
        DateRangeBound.ParseFrom(null).Should().BeNull();
        DateRangeBound.ParseTo(null).Should().BeNull();
    }

    [Fact]
    public void ParseTo_ShouldThrowFormatException_WhenValueIsMalformed()
    {
        var act = () => DateRangeBound.ParseTo("not-a-date");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseTo_ShouldNotOverflow_WhenValueIsTheLastRepresentableDay()
    {
        DateRangeBound.ParseTo("9999-12-31").Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void TryParseTo_ShouldCoverTheWholeDay_WhenValueIsDateOnly()
    {
        DateRangeBound.TryParseTo("2026-03-04", out var bound).Should().BeTrue();

        bound.Should().Be(NextDayStart.AddTicks(-TimeSpan.TicksPerMicrosecond));
    }

    [Fact]
    public void TryParseFrom_ShouldStartAtUtcMidnight_WhenValueIsDateOnly()
    {
        DateRangeBound.TryParseFrom("2026-03-04", out var bound).Should().BeTrue();

        bound.Should().Be(DayStart);
    }

    [Fact]
    public void TryParseFromAndTryParseTo_ShouldKeepTheInstant_WhenValueCarriesATime()
    {
        DateRangeBound.TryParseFrom("2026-03-04T06:00:00Z", out var from).Should().BeTrue();
        DateRangeBound.TryParseTo("2026-03-04T12:00:00+00:00", out var to).Should().BeTrue();

        from.Should().Be(DayStart.AddHours(6));
        to.Should().Be(DayStart.AddHours(12));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void TryParseFromAndTryParseTo_ShouldReturnFalse_WhenValueIsAbsentOrMalformed(string? value)
    {
        DateRangeBound.TryParseFrom(value, out _).Should().BeFalse();
        DateRangeBound.TryParseTo(value, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseExclusiveEnd_ShouldEndAtTheNextUtcMidnight_WhenValueIsDateOnly()
    {
        DateRangeBound.TryParseExclusiveEnd("2026-03-04", out var bound).Should().BeTrue();

        bound.Should().Be(NextDayStart);
        bound.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryParseExclusiveEnd_ShouldKeepTheInstant_WhenValueCarriesATime()
    {
        DateRangeBound.TryParseExclusiveEnd("2026-03-04T07:00:00-05:00", out var bound).Should().BeTrue();

        bound.Should().Be(DayStart.AddHours(12));
        bound.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryParseExclusiveEnd_ShouldNotOverflow_WhenValueIsTheLastRepresentableDay()
    {
        DateRangeBound.TryParseExclusiveEnd("9999-12-31", out var bound).Should().BeTrue();

        bound.Should().Be(DateTimeOffset.MaxValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void TryParseExclusiveEnd_ShouldReturnFalse_WhenValueIsAbsentOrMalformed(string? value)
    {
        DateRangeBound.TryParseExclusiveEnd(value, out _).Should().BeFalse();
    }

    [Fact]
    public void DateRangeQueryBounds_ShouldBindDateOnlyValuesToTheWholeUtcDay_WhenParsed()
    {
        DateRangeStart.TryParse("2026-03-04", null, out var start).Should().BeTrue();
        DateRangeEnd.TryParse("2026-03-04", null, out var end).Should().BeTrue();
        DateRangeExclusiveEnd.TryParse("2026-03-04", null, out var exclusiveEnd).Should().BeTrue();

        start.Value.Should().Be(DayStart);
        end.Value.Should().Be(NextDayStart.AddTicks(-TimeSpan.TicksPerMicrosecond));
        exclusiveEnd.Value.Should().Be(NextDayStart);
    }

    [Fact]
    public void DateRangeQueryBounds_ShouldFailToBind_WhenValueIsMalformed()
    {
        DateRangeStart.TryParse("not-a-date", null, out _).Should().BeFalse();
        DateRangeEnd.TryParse("not-a-date", null, out _).Should().BeFalse();
        DateRangeExclusiveEnd.TryParse("not-a-date", null, out _).Should().BeFalse();
    }
}
