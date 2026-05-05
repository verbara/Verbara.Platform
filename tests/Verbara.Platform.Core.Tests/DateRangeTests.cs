namespace Verbara.Platform.Core.Tests;

public class DateRangeTests
{
    [Fact]
    public void Constructor_ShouldCreateRange_WhenEndAfterStart()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(1);

        var range = new DateRange(start, end);

        range.Start.Should().Be(start);
        range.End.Should().Be(end);
        range.Duration.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenEndBeforeStart()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(-1);

        var act = () => new DateRange(start, end);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Contains_ShouldReturnTrue_WhenPointWithinRange()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(2);
        var range = new DateRange(start, end);

        range.Contains(start.AddHours(1)).Should().BeTrue();
    }

    [Fact]
    public void Contains_ShouldReturnFalse_WhenPointOutsideRange()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(2);
        var range = new DateRange(start, end);

        range.Contains(start.AddHours(3)).Should().BeFalse();
    }
}
