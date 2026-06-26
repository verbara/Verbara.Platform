using Verbara.Platform.Billing;

namespace Verbara.Platform.Billing.Tests;

public class DunningConfigTests
{
    [Fact]
    public void OverageGraceDays_ShouldDefaultToThree_WhenNotSet()
    {
        var config = new DunningConfig();

        config.OverageGraceDays.Should().Be(3);
    }

    [Fact]
    public void PaymentTermDays_ShouldDefaultToFourteen_WhenNotSet()
    {
        var config = new DunningConfig();

        config.PaymentTermDays.Should().Be(14);
    }

    [Fact]
    public void OverageGraceDays_ShouldReturnZero_WhenSetNegative()
    {
        var config = new DunningConfig { OverageGraceDays = -5 };

        config.OverageGraceDays.Should().Be(0);
    }

    [Fact]
    public void PaymentTermDays_ShouldReturnZero_WhenSetNegative()
    {
        var config = new DunningConfig { PaymentTermDays = -1 };

        config.PaymentTermDays.Should().Be(0);
    }

    [Fact]
    public void OverageGraceDays_ShouldReturnAssignedValue_WhenSetPositive()
    {
        var config = new DunningConfig { OverageGraceDays = 5 };

        config.OverageGraceDays.Should().Be(5);
    }

    [Fact]
    public void PaymentTermDays_ShouldReturnAssignedValue_WhenSetPositive()
    {
        var config = new DunningConfig { PaymentTermDays = 30 };

        config.PaymentTermDays.Should().Be(30);
    }
}
