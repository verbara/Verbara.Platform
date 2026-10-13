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
}
