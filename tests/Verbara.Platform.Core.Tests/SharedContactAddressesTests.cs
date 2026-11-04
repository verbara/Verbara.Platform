using Verbara.Platform.Core;

namespace Verbara.Platform.Core.Tests;

/// <summary>
/// contact-history-opens-by-assignment task 1.7 (design D18, Platform/ADR-0047 Decision 5): the sentinel addresses
/// that make a contact shared by many callers. A contact holding one never opens other callers' conversations to an
/// agent. Today the list holds one entry, the voice caller with a withheld number; a new sentinel is one more entry,
/// and this test enumerates the list so the addition is deliberate.
/// </summary>
public sealed class SharedContactAddressesTests
{
    [Fact]
    public void All_ShouldHoldExactlyTheAnonymousVoiceCaller_WhenEnumerated()
    {
        SharedContactAddresses.All.Should().Equal(
            [new ChannelAddress(ChannelType.Voice, "anonymous")],
            "the anonymous voice contact is the only per-tenant sentinel (design D18 sweep)");
        SharedContactAddresses.AnonymousVoiceCaller.Should().Be("anonymous");
    }

    [Theory]
    [InlineData(ChannelType.Voice, "anonymous", true)]
    [InlineData(ChannelType.Voice, "Anonymous", true)]
    [InlineData(ChannelType.Voice, "+15550001111", false)]
    [InlineData(ChannelType.WhatsApp, "anonymous", false)]
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
        SharedContactAddresses.IsShared([new ChannelAddress(ChannelType.Sms, "+15550001111")]).Should().BeFalse();
        SharedContactAddresses.IsShared([]).Should().BeFalse();
    }
}
