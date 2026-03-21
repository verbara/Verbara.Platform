namespace Asterisk.Platform.Core.Tests;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_ShouldReturnCurrentTime()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;

        var now = clock.UtcNow;

        var after = DateTimeOffset.UtcNow;
        now.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
