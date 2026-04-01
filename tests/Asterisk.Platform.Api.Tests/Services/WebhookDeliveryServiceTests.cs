using Asterisk.Platform.Api.Services;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests.Services;

public class WebhookDeliveryServiceTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(2, 300)]
    [InlineData(3, 1800)]
    [InlineData(4, 7200)]
    [InlineData(5, 18000)]
    [InlineData(6, 28800)]
    [InlineData(7, 28800)]
    [InlineData(100, 28800)] // Beyond array bounds clamps to last
    public void GetBackoffSeconds_ShouldReturnExpectedDelay(int attempt, int expectedSeconds)
    {
        WebhookDeliveryService.GetBackoffSeconds(attempt).Should().Be(expectedSeconds);
    }

    [Fact]
    public void BackoffSchedule_ShouldTotalApproximately24Hours()
    {
        var totalSeconds = 0;
        for (int i = 0; i < 8; i++)
            totalSeconds += WebhookDeliveryService.GetBackoffSeconds(i);

        // Total should be ~84660 seconds (~23.5 hours)
        totalSeconds.Should().BeInRange(80000, 90000);
    }
}
