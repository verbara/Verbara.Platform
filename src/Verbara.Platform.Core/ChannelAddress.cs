namespace Verbara.Platform.Core;

public sealed record ChannelAddress
{
    public ChannelType Channel { get; }
    public string Address { get; }

    public ChannelAddress(ChannelType channel, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        Channel = channel;
        Address = address;
    }

    /// <summary>
    /// True when this is a WhatsApp business-scoped user id (BSUID) rather than a phone number: Meta writes a
    /// BSUID as an ISO 3166 alpha-2 country code, a period and up to 128 alphanumerics
    /// (<c>US.13491208655302741918</c>; a parent BSUID adds <c>ENT.</c>), while a <c>wa_id</c> is digits only.
    /// The Cloud API cannot send to a BSUID yet, so a sender never picks one.
    /// </summary>
    public bool IsWhatsAppBusinessScopedUserId() =>
        Channel == ChannelType.WhatsApp
        && Address.Length > 3
        && char.IsAsciiLetter(Address[0])
        && char.IsAsciiLetter(Address[1])
        && Address[2] == '.';

    public override string ToString() =>
        $"{Channel.ToString().ToLowerInvariant()}:{Address}";
}
