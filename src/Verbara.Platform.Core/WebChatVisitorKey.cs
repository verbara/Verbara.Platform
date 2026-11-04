using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Verbara.Platform.Core;

/// <summary>
/// Builds the WebChat level-1 key from the <c>visitorId</c> a browser sends. Contact addresses are visible to
/// agents and the id would let anyone who reads it return as that visitor, so the key holds its SHA-256 and never
/// the id. The input is a random 122-bit value, so no salt is needed, and an equality lookup still works.
/// </summary>
public static class WebChatVisitorKey
{
    private const int HyphenatedLength = 36;
    private const int CompactLength = 32;

    /// <summary>
    /// Gives the key of <paramref name="visitorId"/> when it is a random version-4 GUID, in any case, with or without
    /// hyphens. Anything else gets no key: a constant such as the nil GUID would gather every browser that sends it
    /// into one contact, and a chosen low-entropy id would let unrelated visitors collide.
    /// </summary>
    public static bool TryCreate(string? visitorId, [NotNullWhen(true)] out ChannelAddress? key)
    {
        key = null;
        if (!TryNormalize(visitorId, out var normalized))
            return false;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        key = new ChannelAddress(
            ChannelType.WebChat,
            ChannelAddress.WebChatVisitorKeyPrefix + Base64Url.EncodeToString(hash));
        return true;
    }

    // Reads the characters itself, so only hex digits and hyphens in their places are ever accepted.
    private static bool TryNormalize(string? visitorId, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (visitorId is null || (visitorId.Length != HyphenatedLength && visitorId.Length != CompactLength))
            return false;

        var hyphenated = visitorId.Length == HyphenatedLength;
        var hex = new StringBuilder(CompactLength);
        for (var i = 0; i < visitorId.Length; i++)
        {
            var c = visitorId[i];
            if (hyphenated && i is 8 or 13 or 18 or 23)
            {
                if (c != '-')
                    return false;
            }
            else if (char.IsAsciiHexDigit(c))
            {
                hex.Append(char.ToLowerInvariant(c));
            }
            else
            {
                return false;
            }
        }

        var text = hex.ToString();
        if (text[12] != '4' || text[16] is not ('8' or '9' or 'a' or 'b'))
            return false;

        normalized = $"{text[..8]}-{text[8..12]}-{text[12..16]}-{text[16..20]}-{text[20..]}";
        return true;
    }
}
