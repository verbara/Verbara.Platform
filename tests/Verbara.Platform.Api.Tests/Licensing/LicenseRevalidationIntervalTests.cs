using Verbara.Platform.Api.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Licensing;

/// <summary>
/// The licence gate and the guard trust the last validation result, so the periodic revalidation
/// is what turns a licence that runs out while the process is up into Expired. The host must never
/// hand Pro an interval that switches the timer off or puts it out of reach.
/// </summary>
public sealed class LicenseRevalidationIntervalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-timespan")]
    [InlineData("00:00:00")]
    [InlineData("-00:00:00.001")]
    [InlineData("-1.00:00:00")]
    public void Resolve_ShouldReturnDefault_WhenValueDisablesOrIsUnreadable(string? raw)
    {
        LicenseRevalidationInterval.Resolve(raw).Should().Be(LicenseRevalidationInterval.Default);
    }

    [Fact]
    public void Resolve_ShouldReturnDefault_WhenValueIsInfinite()
    {
        LicenseRevalidationInterval.Resolve(Timeout.InfiniteTimeSpan.ToString())
            .Should().Be(LicenseRevalidationInterval.Default);
    }

    [Theory]
    [InlineData("1.00:00:01")]
    [InlineData("49.00:00:00")]
    [InlineData("10675199.02:48:05.4775807")]
    public void Resolve_ShouldCapAtMaximum_WhenValueExceedsIt(string raw)
    {
        LicenseRevalidationInterval.Resolve(raw).Should().Be(LicenseRevalidationInterval.Maximum);
    }

    [Theory]
    [InlineData("00:00:01", 0, 0, 1)]
    [InlineData("00:30:00", 0, 30, 0)]
    [InlineData("06:00:00", 6, 0, 0)]
    [InlineData("1.00:00:00", 24, 0, 0)]
    public void Resolve_ShouldKeepValue_WhenWithinRange(string raw, int hours, int minutes, int seconds)
    {
        LicenseRevalidationInterval.Resolve(raw).Should().Be(new TimeSpan(hours, minutes, seconds));
    }

    [Fact]
    public void Default_ShouldBeSixHours_WhenRead()
    {
        LicenseRevalidationInterval.Default.Should().Be(TimeSpan.FromHours(6));
        LicenseRevalidationInterval.Maximum.Should().Be(TimeSpan.FromHours(24));
    }
}
