using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Typification.Tests;

public sealed class PiiScreenTests
{
    private static PiiPolicy Allow(params PiiType[] types) =>
        new() { AllowStore = new HashSet<PiiType>(types) };

    [Fact]
    public void Apply_ShouldMaskCard_WhenNotAllowListed()
    {
        var (value, masked) = PiiScreen.Apply("4111111111111111", PiiPolicy.DenyAll);

        value.Should().Be("[CARD]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldPassValue_WhenTypeAllowListed()
    {
        const string original = "4111111111111111";

        var (value, masked) = PiiScreen.Apply(original, Allow(PiiType.Card));

        value.Should().Be(original);
        masked.Should().BeFalse();
    }

    [Fact]
    public void Apply_ShouldMaskNationalId_ByDefault()
    {
        var (value, masked) = PiiScreen.Apply("123-45-6789", PiiPolicy.DenyAll);

        value.Should().Be("[NATIONAL_ID]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldMaskEmail_WhenNotAllowListed()
    {
        var (value, masked) = PiiScreen.Apply("john@example.com", PiiPolicy.DenyAll);

        value.Should().Be("[EMAIL]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldMaskPhone_WhenNotAllowListed()
    {
        var (value, masked) = PiiScreen.Apply("+1 415-555-0132", PiiPolicy.DenyAll);

        value.Should().Be("[PHONE]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldNotMaskNonPii_WhenPlainText()
    {
        var (value, masked) = PiiScreen.Apply("renewal request", PiiPolicy.DenyAll);

        value.Should().Be("renewal request");
        masked.Should().BeFalse();
    }

    [Fact]
    public void Apply_ShouldNotMaskLongOrderNumber_WhenFailsLuhn()
    {
        // 1234567812345678 fails the Luhn check, so it must NOT be masked as a card.
        var (value, masked) = PiiScreen.Apply("1234567812345678", PiiPolicy.DenyAll);

        value.Should().NotContain("[CARD]");
        masked.Should().BeFalse();
    }

    [Fact]
    public void Apply_ShouldMaskOnlyDetectedSpan_WhenValueHasSurroundingText()
    {
        var (value, masked) = PiiScreen.Apply("card 4111111111111111 please", PiiPolicy.DenyAll);

        value.Should().Be("card [CARD] please");
        masked.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_ShouldReturnEmpty_WhenValueEmpty(string input)
    {
        var (value, masked) = PiiScreen.Apply(input, PiiPolicy.DenyAll);

        value.Should().Be(input);
        masked.Should().BeFalse();
    }
}
