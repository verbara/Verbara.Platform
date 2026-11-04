using Verbara.Platform.Core;

namespace Verbara.Platform.Core.Tests;

/// <summary>
/// contact-history-opens-by-assignment task 1.7 (design D18, Platform/ADR-0047 Decision 5): the sentinel addresses
/// that make a contact shared by many callers. A contact holding one never opens other callers' conversations to an
/// agent. The list holds the voice caller with no number: the bridge's own <c>anonymous</c> bucket, plus the
/// placeholder strings Asterisk and carriers send in place of a number (review round 1). A new sentinel is one more
/// entry, and this test enumerates the list so the addition is deliberate.
/// </summary>
public sealed class SharedContactAddressesTests
{
    [Fact]
    public void All_ShouldHoldTheAnonymousVoiceCallerAndTheWithheldPlaceholders_WhenEnumerated()
    {
        SharedContactAddresses.All.Should().Equal(
            [
                new ChannelAddress(ChannelType.Voice, "anonymous"),
                new ChannelAddress(ChannelType.Voice, "<unknown>"),
                new ChannelAddress(ChannelType.Voice, "unknown"),
                new ChannelAddress(ChannelType.Voice, "restricted"),
                new ChannelAddress(ChannelType.Voice, "unavailable"),
                new ChannelAddress(ChannelType.Voice, "private"),
            ],
            "the voice caller with no number is the only per-tenant sentinel (design D18 sweep), under every string it arrives as");
        SharedContactAddresses.AnonymousVoiceCaller.Should().Be("anonymous");
    }

    [Theory]
    [InlineData(ChannelType.Voice, "anonymous", true)]
    [InlineData(ChannelType.Voice, "Anonymous", true)]
    [InlineData(ChannelType.Voice, "<unknown>", true)]
    [InlineData(ChannelType.Voice, "<UNKNOWN>", true)]
    [InlineData(ChannelType.Voice, "Unknown", true)]
    [InlineData(ChannelType.Voice, "Restricted", true)]
    [InlineData(ChannelType.Voice, "UNAVAILABLE", true)]
    [InlineData(ChannelType.Voice, "Private", true)]
    [InlineData(ChannelType.Voice, " <unknown> ", true)]
    [InlineData(ChannelType.Voice, "+15550001111", false)]
    [InlineData(ChannelType.Voice, "unknown-1001", false)]
    [InlineData(ChannelType.WhatsApp, "anonymous", false)]
    [InlineData(ChannelType.Sms, "<unknown>", false)]
    [InlineData(ChannelType.WebChat, "webchat-anonymous", false)]
    public void IsShared_ShouldMatchTheSentinelOnItsChannel_WhenGivenAnAddress(ChannelType channel, string address, bool shared)
    {
        SharedContactAddresses.IsShared(new ChannelAddress(channel, address)).Should().Be(shared);
    }

    [Fact]
    public void IsShared_ShouldBeTrue_WhenAnyAddressOfTheContactIsASentinel()
    {
        SharedContactAddresses.IsShared(
                [new ChannelAddress(ChannelType.Sms, "+15550001111"), new ChannelAddress(ChannelType.Voice, "anonymous")])
            .Should().BeTrue();
        SharedContactAddresses.IsShared(
                [new ChannelAddress(ChannelType.Sms, "+15550001111"), new ChannelAddress(ChannelType.Voice, "<unknown>")])
            .Should().BeTrue();
        SharedContactAddresses.IsShared([new ChannelAddress(ChannelType.Sms, "+15550001111")]).Should().BeFalse();
        SharedContactAddresses.IsShared([]).Should().BeFalse();
    }

    // Review round 1: Asterisk's AMI reports a missing caller id as the literal "<unknown>", and carriers send
    // "Restricted", "Unavailable" or "Private"; none of them identifies a caller, so every one resolves to the tenant's
    // one anonymous voice contact.
    [Theory]
    [InlineData(null, "anonymous")]
    [InlineData("", "anonymous")]
    [InlineData("   ", "anonymous")]
    [InlineData("anonymous", "anonymous")]
    [InlineData("<unknown>", "anonymous")]
    [InlineData("unknown", "anonymous")]
    [InlineData("Restricted", "anonymous")]
    [InlineData("Unavailable", "anonymous")]
    [InlineData("PRIVATE", "anonymous")]
    [InlineData(" <unknown> ", "anonymous")]
    [InlineData("+15550001111", "+15550001111")]
    [InlineData("1001", "1001")]
    public void VoiceCallerAddressOf_ShouldMapEveryWithheldCallerIdToTheAnonymousCaller_WhenGivenACallerIdNumber(
        string? callerIdNum, string expected)
    {
        SharedContactAddresses.VoiceCallerAddressOf(callerIdNum).Should().Be(expected);
    }
}
