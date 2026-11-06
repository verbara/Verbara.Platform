namespace Verbara.Platform.Core;

/// <summary>
/// When two contacts hold the same address on a channel (contacts-writes-follow-their-permissions, design D10): the one
/// reduction the dial, the start and every contact store compare. A phone-like channel compares
/// <see cref="VoiceNumber.Canonical"/>; every other channel compares the address as stored, with ASCII blanks (space,
/// tab, CR, LF) trimmed from both ends and nothing else changed. An address that reduces to the empty string matches
/// nothing.
/// </summary>
public static class ChannelAddressMatch
{
    private static readonly char[] Blanks = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// Whether <paramref name="channel"/> addresses a contact by a telephone number: <see cref="ChannelType.Voice"/>,
    /// <see cref="ChannelType.Sms"/>, <see cref="ChannelType.WhatsApp"/> and <see cref="ChannelType.Rcs"/>. Their
    /// providers deliver to a subscriber's telephone number, so one person is written with spaces, dashes or brackets on
    /// one contact and bare on another. As for Voice, a leading plus is kept and no country or trunk prefix is added or
    /// removed. Every other channel addresses a provider-issued id, a session key or an email address, where punctuation
    /// is part of the address.
    /// </summary>
    public static bool IsPhoneLike(ChannelType channel) =>
        channel is ChannelType.Voice or ChannelType.Sms or ChannelType.WhatsApp or ChannelType.Rcs;

    /// <summary>The form of <paramref name="address"/> that two holders of the same address on <paramref name="channel"/> share.</summary>
    public static string KeyOf(ChannelType channel, string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IsPhoneLike(channel) ? VoiceNumber.Canonical(address) : address.Trim(Blanks);
    }
}
