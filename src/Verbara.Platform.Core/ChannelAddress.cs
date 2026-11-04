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

    /// <summary>The prefix of a WebChat level-1 key, which recognises a browser and proves nothing.</summary>
    public const string WebChatVisitorKeyPrefix = "webchat-visitor:";

    /// <summary>The prefix of a WebChat level-2 key, the customer id a site signed for.</summary>
    public const string WebChatExternalKeyPrefix = "webchat-external:";

    /// <summary>True when this is a WebChat level-1 key. The test is ordinal and case-sensitive.</summary>
    public bool IsWebChatVisitorKey() => HasWebChatPrefix(WebChatVisitorKeyPrefix);

    /// <summary>True when this is a WebChat level-2 key. The test is ordinal and case-sensitive.</summary>
    public bool IsWebChatExternalKey() => HasWebChatPrefix(WebChatExternalKeyPrefix);

    /// <summary>
    /// True when this WebChat address identifies a contact rather than a session. An identity key is never a
    /// destination. The contact's identity level, the reply destination and the check on contact writes all use
    /// this one test, so they cannot disagree: <c>WebChat-External:x</c> is an ordinary address for all three.
    /// </summary>
    public bool IsWebChatIdentityKey() => IsWebChatVisitorKey() || IsWebChatExternalKey();

    private bool HasWebChatPrefix(string prefix) =>
        Channel == ChannelType.WebChat && Address.StartsWith(prefix, StringComparison.Ordinal);

    public override string ToString() =>
        $"{Channel.ToString().ToLowerInvariant()}:{Address}";
}
