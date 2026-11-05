using Verbara.Platform.Api.Services.OutboundTrunk;

namespace Verbara.Platform.Api.Tests.OutboundTrunk;

/// <summary>outbound-calls-reach-the-trunk (design D5): a dialed number keeps its digits and a single leading <c>+</c>.</summary>
public sealed class DialNumberTests
{
    [Theory]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData("57 300", "57300")]
    [InlineData("1+2", "12")]
    [InlineData("++57", "+57")]
    [InlineData("abc", "")]
    [InlineData("", "")]
    [InlineData(" +57-300", "+57300")]
    public void Normalize_ShouldKeepDigitsAndOneLeadingPlus_WhenGivenARawNumber(string raw, string expected) =>
        DialNumber.Normalize(raw).Should().Be(expected);

    /// <summary>
    /// contacts-writes-follow-their-permissions task 1.6 (design D10): the dial's normalizer and the canonical form the
    /// contact stores compare are one algorithm, so the dial and every store reduce a number alike.
    /// </summary>
    [Theory]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData("1 (555) 123-4567", "15551234567")]
    [InlineData(" +1-555", "+1555")]
    [InlineData("\t\r\n+1555", "+1555")]
    [InlineData("\u00A0+1555", "1555")]
    [InlineData("\u3000+1555", "1555")]
    [InlineData("1+555", "1555")]
    [InlineData("anonymous", "")]
    [InlineData("+", "")]
    [InlineData("+abc", "")]
    public void Normalize_ShouldEqualTheCanonicalVoiceNumber_WhenGivenTheSharedTable(string raw, string expected)
    {
        DialNumber.Normalize(raw).Should().Be(expected);
        DialNumber.Normalize(raw).Should().Be(Verbara.Platform.Core.VoiceNumber.Canonical(raw));
    }
}
