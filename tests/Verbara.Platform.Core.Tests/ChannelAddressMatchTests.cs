namespace Verbara.Platform.Core.Tests;

/// <summary>
/// contacts-writes-follow-their-permissions, review round (design D10): which channels compare the canonical number and
/// which compare the address as stored with ASCII blanks trimmed.
/// </summary>
public sealed class ChannelAddressMatchTests
{
    [Theory]
    [InlineData(ChannelType.Voice, true)]
    [InlineData(ChannelType.Sms, true)]
    [InlineData(ChannelType.WhatsApp, true)]
    [InlineData(ChannelType.Rcs, true)]
    [InlineData(ChannelType.WebChat, false)]
    [InlineData(ChannelType.Email, false)]
    [InlineData(ChannelType.Messenger, false)]
    [InlineData(ChannelType.Instagram, false)]
    [InlineData(ChannelType.Telegram, false)]
    [InlineData(ChannelType.Twitter, false)]
    [InlineData(ChannelType.Video, false)]
    public void IsPhoneLike_ShouldBeTrueOnlyForTheChannelsThatAddressATelephoneNumber_WhenAskedForEachChannel(ChannelType channel, bool phoneLike)
    {
        ChannelAddressMatch.IsPhoneLike(channel).Should().Be(phoneLike);
        Enum.GetValues<ChannelType>().Should().HaveCount(11, "a new channel must be placed on one side of this table");
    }

    [Theory]
    [InlineData(ChannelType.WhatsApp, " +1 (555) 123-4567", "+15551234567")]
    [InlineData(ChannelType.Sms, "1-555-123-4567", "15551234567")]
    [InlineData(ChannelType.WhatsApp, "+", "")]
    [InlineData(ChannelType.Email, "\t Ana@Example.com \r\n", "Ana@Example.com")]
    [InlineData(ChannelType.Telegram, " 12 34 ", "12 34")]
    [InlineData(ChannelType.Email, " ana@example.com", " ana@example.com")]
    public void KeyOf_ShouldReduceTheAddressByTheChannelsRule_WhenGivenAStoredForm(ChannelType channel, string address, string key)
    {
        ChannelAddressMatch.KeyOf(channel, address).Should().Be(key);
    }
}
