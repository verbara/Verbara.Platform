namespace Verbara.Platform.Billing.Tests;

/// <summary>
/// partner-channel-pilot-fixes 1.5 (rate-card-settlement-currency, design D2): the static ISO 4217 code set.
/// ISO list revision pinned here: ISO 4217 List One currency and fund codes as of 2026-10-11 (XCG for ANG,
/// ZWG for ZWL, SLE for SLL, BGN withdrawn on 2026-01-01); non-currency codes (metals, XDR, XTS, XXX) excluded.
/// </summary>
public sealed class Iso4217Tests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("PEN")]
    [InlineData("EUR")]
    [InlineData("MXN")]
    [InlineData("COP")]
    public void TryCanonicalize_ShouldAccept_WhenCodeIsActiveIso4217(string code)
    {
        Iso4217.TryCanonicalize(code, out var canonical).Should().BeTrue();
        canonical.Should().Be(code);
        Iso4217.IsValid(code).Should().BeTrue();
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("US")]
    [InlineData("XYZ123")]
    [InlineData("XXX")]
    [InlineData("ANG")]
    public void TryCanonicalize_ShouldRefuse_WhenValueIsNotAnActiveCode(string? value)
    {
        Iso4217.TryCanonicalize(value, out var canonical).Should().BeFalse();
        canonical.Should().BeEmpty();
        Iso4217.IsValid(value).Should().BeFalse();
    }

    [Fact]
    public void TryCanonicalize_ShouldTrimAndUpperCase_WhenCodeHasSpacesAndLowerCase()
    {
        Iso4217.TryCanonicalize(" usd ", out var canonical).Should().BeTrue();
        canonical.Should().Be("USD");
    }
}
