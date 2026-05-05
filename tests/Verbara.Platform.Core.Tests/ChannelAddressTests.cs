namespace Verbara.Platform.Core.Tests;

public class ChannelAddressTests
{
    [Fact]
    public void Constructor_ShouldCreateAddress_WhenValidInput()
    {
        var address = new ChannelAddress(ChannelType.WhatsApp, "+1234567890");

        address.Channel.Should().Be(ChannelType.WhatsApp);
        address.Address.Should().Be("+1234567890");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenAddressEmpty()
    {
        var act = () => new ChannelAddress(ChannelType.Sms, "");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Equality_ShouldBeTrue_WhenSameChannelAndAddress()
    {
        var a = new ChannelAddress(ChannelType.Email, "test@example.com");
        var b = new ChannelAddress(ChannelType.Email, "test@example.com");

        a.Should().Be(b);
    }

    [Fact]
    public void Equality_ShouldBeFalse_WhenDifferentChannel()
    {
        var a = new ChannelAddress(ChannelType.WhatsApp, "+1234567890");
        var b = new ChannelAddress(ChannelType.Sms, "+1234567890");

        a.Should().NotBe(b);
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        var address = new ChannelAddress(ChannelType.WhatsApp, "+1234567890");

        address.ToString().Should().Be("whatsapp:+1234567890");
    }
}
