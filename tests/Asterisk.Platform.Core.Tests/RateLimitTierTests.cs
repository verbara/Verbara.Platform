namespace Asterisk.Platform.Core.Tests;

public class RateLimitTierTests
{
    [Theory]
    [InlineData(RateLimitTier.Free, 60)]
    [InlineData(RateLimitTier.Standard, 300)]
    [InlineData(RateLimitTier.Professional, 600)]
    [InlineData(RateLimitTier.Enterprise, 1200)]
    [InlineData(RateLimitTier.Unlimited, 0)]
    public void GetPermitLimit_ShouldReturnCorrectLimit_WhenTierIsKnown(RateLimitTier tier, int expected)
    {
        tier.GetPermitLimit().Should().Be(expected);
    }

    [Fact]
    public void IsUnlimited_ShouldReturnTrue_WhenTierIsUnlimited()
    {
        RateLimitTier.Unlimited.IsUnlimited().Should().BeTrue();
    }

    [Theory]
    [InlineData(RateLimitTier.Free)]
    [InlineData(RateLimitTier.Standard)]
    [InlineData(RateLimitTier.Professional)]
    [InlineData(RateLimitTier.Enterprise)]
    public void IsUnlimited_ShouldReturnFalse_WhenTierIsNotUnlimited(RateLimitTier tier)
    {
        tier.IsUnlimited().Should().BeFalse();
    }

    [Fact]
    public void Free_ShouldHaveLowestLimit_WhenComparedToOtherLimitedTiers()
    {
        var freeLimit = RateLimitTier.Free.GetPermitLimit();
        freeLimit.Should().BeLessThan(RateLimitTier.Standard.GetPermitLimit());
        freeLimit.Should().BeLessThan(RateLimitTier.Professional.GetPermitLimit());
        freeLimit.Should().BeLessThan(RateLimitTier.Enterprise.GetPermitLimit());
    }
}
