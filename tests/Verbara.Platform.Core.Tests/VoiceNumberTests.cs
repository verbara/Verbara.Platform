namespace Verbara.Platform.Core.Tests;

/// <summary>
/// contacts-writes-follow-their-permissions task 1.6 (design D10): the canonical form of a Voice number keeps the ASCII
/// digits and a leading <c>+</c> when the first character that is not an ASCII blank (space, tab, CR, LF) is <c>+</c>.
/// A digitless input is empty, never a lone <c>+</c>. <c>DialNumberTests</c> pins the dial's normalizer to this table.
/// </summary>
public sealed class VoiceNumberTests
{
    /// <summary>The shared table: raw input and its canonical form.</summary>
    public static TheoryData<string, string> Cases() => new()
    {
        { "+1 (555) 123-4567", "+15551234567" },
        { "1 (555) 123-4567", "15551234567" },
        { " +1-555", "+1555" },
        { "\t\r\n+1555", "+1555" },
        { " +1555", "1555" },
        { "　+1555", "1555" },
        { "1+555", "1555" },
        { "anonymous", "" },
        { "+", "" },
        { "+abc", "" },
        { "", "" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Canonical_ShouldKeepTheDigitsAndALeadingPlusAfterAsciiBlanksOnly_WhenGivenARawNumber(string raw, string expected) =>
        VoiceNumber.Canonical(raw).Should().Be(expected);
}
