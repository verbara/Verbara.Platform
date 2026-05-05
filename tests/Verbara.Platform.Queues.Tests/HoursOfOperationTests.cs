using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Queues.Tests;

public class HoursOfOperationTests
{
    [Fact]
    public void IsOpen_ShouldReturnTrue_WhenWithinHours()
    {
        var hours = new HoursOfOperation("America/New_York");
        hours.SetDaySchedule(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0));

        // A Monday at 10 AM ET
        var monday10am = new DateTimeOffset(2026, 3, 23, 14, 0, 0, TimeSpan.Zero); // 10 AM ET = 14:00 UTC (EDT)

        hours.IsOpen(monday10am).Should().BeTrue();
    }

    [Fact]
    public void IsOpen_ShouldReturnFalse_WhenOutsideHours()
    {
        var hours = new HoursOfOperation("America/New_York");
        hours.SetDaySchedule(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0));

        // A Monday at 8 PM ET
        var monday8pm = new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.Zero); // 8 PM ET = 00:00 UTC next day (EDT)

        hours.IsOpen(monday8pm).Should().BeFalse();
    }

    [Fact]
    public void IsOpen_ShouldReturnFalse_WhenNoDayScheduleSet()
    {
        var hours = new HoursOfOperation("UTC");

        // Sunday with no schedule
        var sunday = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);

        hours.IsOpen(sunday).Should().BeFalse();
    }

    [Fact]
    public void IsOpen247_ShouldReturnTrue_WhenAllDaysFullCoverage()
    {
        var hours = HoursOfOperation.AlwaysOpen();

        var anytime = DateTimeOffset.UtcNow;
        hours.IsOpen(anytime).Should().BeTrue();
    }
}
