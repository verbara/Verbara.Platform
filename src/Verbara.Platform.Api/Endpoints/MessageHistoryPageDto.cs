using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// <c>GET /conversations/{id}/history</c> (contact-history-opens-by-assignment, design D10;
/// <c>conversation-message-history.v1.json</c>): one newest-first page of the transcript in the existing
/// <see cref="Message"/> shape, whether an older message exists, and the opaque cursor that reads the next older page
/// (absent when <paramref name="HasMore"/> is false).
/// </summary>
internal sealed record MessageHistoryPageDto(IReadOnlyList<Message> Items, bool HasMore, string? NextBefore);

/// <summary>
/// The opaque <c>before</c> / <c>nextBefore</c> cursor of the history route (design D10): base64url of
/// <c>"&lt;createdAt UTC ticks&gt;:&lt;messageId&gt;"</c>, built from a page's last item. The client never parses it.
/// </summary>
internal static class MessageHistoryCursorCodec
{
    /// <summary>The refusal code of a <c>before</c> the server cannot read: 400 <c>{"error":"invalid-cursor"}</c>.</summary>
    public const string InvalidCursor = "invalid-cursor";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The cursor naming <paramref name="last"/>, the last item of a page.</summary>
    public static string Encode(Message last)
    {
        ArgumentNullException.ThrowIfNull(last);
        var text = string.Concat(
            last.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), ":", last.MessageId.Value);
        return Base64Url.EncodeToString(StrictUtf8.GetBytes(text));
    }

    /// <summary>
    /// Reads a cursor this codec wrote; <see langword="false"/> for any other value: not base64url, not UTF-8, no
    /// colon, a tick part that is not plain ASCII digits or out of the <see cref="DateTime"/> range, or an id that is
    /// empty, only white space, or holds a NUL (no id is either, <see cref="EntityId.From"/> throws on white space and
    /// Postgres refuses a NUL in text).
    /// </summary>
    public static bool TryDecode(string value, out MessageHistoryCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value) || !Base64Url.IsValid(value))
            return false;

        string text;
        try
        {
            text = StrictUtf8.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return false;

        var messageId = text[(colon + 1)..];
        if (!EntityId.IsValid(messageId) || messageId.Contains('\0', StringComparison.Ordinal))
            return false;

        // NumberStyles.None: ASCII digits only, no sign, no white space, no exponent.
        if (!long.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks)
            return false;

        cursor = new MessageHistoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), EntityId.From(messageId));
        return true;
    }
}
