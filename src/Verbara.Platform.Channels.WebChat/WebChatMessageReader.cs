using System.Net.WebSockets;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>What one <see cref="WebChatMessageReader.ReadAsync"/> call found on the visitor socket.</summary>
public enum WebChatFrameKind
{
    /// <summary>A whole text message, within the byte bound; its bytes are in <see cref="WebChatInboundFrame.Payload"/>.</summary>
    Text,

    /// <summary>A text message larger than the byte bound. It was read to its end and discarded.</summary>
    Oversized,

    /// <summary>A message that is not text (binary). It was read to its end and discarded.</summary>
    Ignored,

    /// <summary>The visitor closed the socket.</summary>
    Close,
}

/// <summary>One message read from the visitor socket.</summary>
/// <param name="Kind">What was read.</param>
/// <param name="Payload">
/// The message's UTF-8 bytes when <paramref name="Kind"/> is <see cref="WebChatFrameKind.Text"/>, empty otherwise. It
/// is valid only until the next <see cref="WebChatMessageReader.ReadAsync"/> call, which reuses the buffer.
/// </param>
public readonly record struct WebChatInboundFrame(WebChatFrameKind Kind, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads whole messages from a visitor socket. A WebSocket message can arrive in several reads; this keeps reading
/// until its end, into a buffer bounded by <c>maxMessageBytes</c>. A message over the bound, or one that is not
/// text, is read to its end and discarded, so the socket stays usable for the next message (spec
/// <c>webchat-visitor-identity</c>). One reader serves one socket; it is not thread-safe.
/// </summary>
public sealed class WebChatMessageReader
{
    /// <summary>Room for the JSON envelope around the text (<c>{"type":"message","text":"…"}</c>) and its whitespace.</summary>
    public const int EnvelopeAllowanceBytes = 1024;

    // A UTF-16 code unit takes at most 3 UTF-8 bytes (a surrogate pair, 2 units, takes 4), and a JSON encoder may
    // write any character as a 6-byte \uXXXX escape: 6 bytes per character covers every client.
    private const int MaxJsonBytesPerCharacter = 6;
    private const int DrainChunkBytes = 4096;

    private readonly byte[] _buffer;
    private byte[]? _drain;

    public WebChatMessageReader(int maxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        _buffer = new byte[maxMessageBytes];
    }

    /// <summary>
    /// The byte bound that always fits a message of <paramref name="maxMessageLength"/> characters, however the
    /// client escapes it.
    /// </summary>
    public static int MaxMessageBytesFor(int maxMessageLength) =>
        checked(maxMessageLength * MaxJsonBytesPerCharacter + EnvelopeAllowanceBytes);

    /// <summary>
    /// Reads the next whole message. A <see cref="WebSocketException"/> from the socket propagates to the caller.
    /// </summary>
    public async Task<WebChatInboundFrame> ReadAsync(WebSocket socket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var count = 0;
        while (true)
        {
            if (count == _buffer.Length)
            {
                // The bound is full and the message has not ended: it is too large. Discard the rest of it.
                return await DrainAsync(socket, WebChatFrameKind.Oversized, ct).ConfigureAwait(false);
            }

            var result = await socket.ReceiveAsync(_buffer.AsMemory(count), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return new WebChatInboundFrame(WebChatFrameKind.Close, ReadOnlyMemory<byte>.Empty);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return result.EndOfMessage
                    ? new WebChatInboundFrame(WebChatFrameKind.Ignored, ReadOnlyMemory<byte>.Empty)
                    : await DrainAsync(socket, WebChatFrameKind.Ignored, ct).ConfigureAwait(false);
            }

            count += result.Count;
            if (result.EndOfMessage)
                return new WebChatInboundFrame(WebChatFrameKind.Text, _buffer.AsMemory(0, count));
        }
    }

    /// <summary>Reads the current message to its end without keeping it.</summary>
    private async Task<WebChatInboundFrame> DrainAsync(WebSocket socket, WebChatFrameKind kind, CancellationToken ct)
    {
        _drain ??= new byte[DrainChunkBytes];
        while (true)
        {
            var result = await socket.ReceiveAsync(_drain.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return new WebChatInboundFrame(WebChatFrameKind.Close, ReadOnlyMemory<byte>.Empty);
            if (result.EndOfMessage)
                return new WebChatInboundFrame(kind, ReadOnlyMemory<byte>.Empty);
        }
    }
}
