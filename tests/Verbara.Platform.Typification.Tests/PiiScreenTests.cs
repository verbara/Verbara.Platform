using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Typification.Tests;

public sealed class PiiScreenTests
{
    private static PiiPolicy Allow(params PiiType[] types) =>
        new() { AllowStore = new HashSet<PiiType>(types) };

    [Fact]
    public void Apply_ShouldTreatNullPolicyAsDenyAll()
    {
        // A null policy (e.g. a pre-D2 config whose source-gen deserialization left
        // PiiPolicy null) must fail CLOSED: mask everything, never NRE.
        var (value, masked) = PiiScreen.Apply("john@example.com", null);

        value.Should().Be("[EMAIL]");
        masked.Should().BeTrue();
    }

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

    [Fact]
    public void Apply_ShouldMaskAllTypes_WhenValueHasMultipleMixedPii()
    {
        var (value, masked) = PiiScreen.Apply(
            "call +1 415-555-0132 or john@example.com card 4111111111111111",
            PiiPolicy.DenyAll);

        value.Should().Be("call [PHONE] or [EMAIL] card [CARD]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldMaskOnlyNonAllowListed_WhenMixedTypesAndPartialAllowList()
    {
        var (value, masked) = PiiScreen.Apply(
            "john@example.com 4111111111111111",
            Allow(PiiType.Email));

        value.Should().Be("john@example.com [CARD]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldMaskCard_WhenWrittenWithSpaces()
    {
        var (value, masked) = PiiScreen.Apply("4111 1111 1111 1111", PiiPolicy.DenyAll);

        value.Should().Be("[CARD]");
        masked.Should().BeTrue();
    }

    [Fact]
    public void Apply_ShouldMaskUnicodeDigitCard_WhenFullwidthDigits()
    {
        // A valid-Luhn card rendered in fullwidth (U+FF10..U+FF19) decimal digits. Before the
        // I-2 fix this slipped through unmasked (\d matched but ASCII Luhn rejected it); after
        // the fix the digits are normalized to ASCII for matching/Luhn and masked in place.
        const string card = "5555555555554444";
        var fullwidth = new string(card.Select(c => (char)('０' + (c - '0'))).ToArray());

        var (value, masked) = PiiScreen.Apply(fullwidth, PiiPolicy.DenyAll);

        value.Should().Be("[CARD]");
        masked.Should().BeTrue();
    }
}
