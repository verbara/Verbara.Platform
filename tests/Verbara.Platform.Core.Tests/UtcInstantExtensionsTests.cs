namespace Verbara.Platform.Core.Tests;

public class UtcInstantExtensionsTests
{
    [Fact]
    public void ToUtcInstant_ShouldReturnSameValue_WhenAlreadyOffsetZero()
    {
        var value = new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero);

        var result = value.ToUtcInstant();

        result.Should().Be(value);
        result.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ToUtcInstant_ShouldNormaliseToOffsetZero_WhenOffsetIsNegative()
    {
        // The UTC-5 case that reproduces the reported defect.
        var value = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(-5));

        var result = value.ToUtcInstant();

        result.Offset.Should().Be(TimeSpan.Zero);
        result.Should().Be(new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToUtcInstant_ShouldNormaliseToOffsetZero_WhenOffsetIsPositive()
    {
        var value = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.FromHours(5));

        var result = value.ToUtcInstant();

        result.Offset.Should().Be(TimeSpan.Zero);
        result.Should().Be(new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToUtcInstant_ShouldPreserveTheInstant_WhenOffsetIsNonZero()
    {
        // Converting must never move the point in time — only how it is expressed.
        var value = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(-5));

        var result = value.ToUtcInstant();

        result.UtcTicks.Should().Be(value.UtcTicks);
    }

    [Fact]
    public void ToUtcInstant_ShouldBeIdempotent_WhenAppliedTwice()
    {
        var value = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(-5));

        var once = value.ToUtcInstant();
        var twice = once.ToUtcInstant();

        twice.Should().Be(once);
    }

    [Fact]
    public void ToUtcInstant_ShouldReturnNull_WhenNullableInputIsNull()
    {
        DateTimeOffset? value = null;

        var result = value.ToUtcInstant();

        result.Should().BeNull();
    }

    [Fact]
    public void ToUtcInstant_ShouldNormalise_WhenNullableInputHasValue()
    {
        DateTimeOffset? value = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(-5));

        var result = value.ToUtcInstant();

        result.Should().NotBeNull();
        result!.Value.Offset.Should().Be(TimeSpan.Zero);
        result.Value.Should().Be(new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToUtcInstant_ShouldNotOverflow_AtTheBoundaries()
    {
        var min = () => DateTimeOffset.MinValue.ToUtcInstant();
        var max = () => DateTimeOffset.MaxValue.ToUtcInstant();

        min.Should().NotThrow();
        max.Should().NotThrow();
        DateTimeOffset.MinValue.ToUtcInstant().Should().Be(DateTimeOffset.MinValue);
        DateTimeOffset.MaxValue.ToUtcInstant().Should().Be(DateTimeOffset.MaxValue);
    }
}
