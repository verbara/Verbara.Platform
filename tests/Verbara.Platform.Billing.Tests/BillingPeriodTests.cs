using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Billing.Tests;

/// <summary>
/// Pins <see cref="BillingPeriod.Current(IClock)"/> to the exact UTC calendar-month boundary the four
/// previously-inlined <c>GetCurrentPeriod()</c> sites computed, so the period-helper extraction is proven
/// to change no boundary. See ADR-0033 / the credit-ledger-substrate spec delta.
/// </summary>
public class BillingPeriodTests
{
    private static IClock ClockAt(DateTimeOffset now)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        return clock;
    }

    [Fact]
    public void Current_ShouldReturnMonthBoundaryAndKey_WhenMidMonth()
    {
        var clock = ClockAt(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));

        var (start, end, key) = BillingPeriod.Current(clock);

        start.Should().Be(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        end.Should().Be(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        key.Should().Be("2026-06");
    }

    [Fact]
    public void Current_ShouldMatchInlinedComputation_ForArbitraryInstant()
    {
        var now = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var clock = ClockAt(now);

        // The exact computation that lived inline at all four sites.
        var expectedStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var expectedEnd = expectedStart.AddMonths(1);

        var (start, end, _) = BillingPeriod.Current(clock);

        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [Fact]
    public void Current_ShouldRollToNextYear_WhenDecember()
    {
        var clock = ClockAt(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero));

        var (start, end, key) = BillingPeriod.Current(clock);

        start.Should().Be(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        end.Should().Be(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        key.Should().Be("2026-12");
    }

    [Fact]
    public void Current_ShouldZeroPadSingleDigitMonth_WhenKeyFormatted()
    {
        var clock = ClockAt(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        var (_, _, key) = BillingPeriod.Current(clock);

        key.Should().Be("2026-01");
    }
}
