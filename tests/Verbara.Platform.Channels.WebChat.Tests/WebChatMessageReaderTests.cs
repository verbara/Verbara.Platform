using System.Net.WebSockets;
using System.Text;

namespace Verbara.Platform.Channels.WebChat.Tests;

public class WebChatMessageReaderTests
{
    [Fact]
    public async Task ReadAsync_ShouldAssembleWholeMessage_WhenItArrivesInSeveralReads()
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("€ñ", 2000)));
        var socket = new ScriptedWebSocket(chunkSize: 1000).Text(payload);
        var reader = new WebChatMessageReader(maxMessageBytes: 16 * 1024);

        var frame = await reader.ReadAsync(socket, CancellationToken.None);

        frame.Kind.Should().Be(WebChatFrameKind.Text);
        frame.Payload.ToArray().Should().Equal(payload, "every read of the message is kept, in order, until its end");
        socket.Reads.Should().BeGreaterThan(1, "the message really did arrive in more than one read");
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnText_WhenMessageIsExactlyTheBound()
    {
        var payload = new byte[64];
        Array.Fill(payload, (byte)'a');
        var socket = new ScriptedWebSocket(chunkSize: 10).Text(payload);

        var frame = await new WebChatMessageReader(maxMessageBytes: 64).ReadAsync(socket, CancellationToken.None);

        frame.Kind.Should().Be(WebChatFrameKind.Text);
        frame.Payload.Length.Should().Be(64);
    }

    [Fact]
    public async Task ReadAsync_ShouldDiscardMessageAndReadNext_WhenMessageExceedsBound()
    {
        var socket = new ScriptedWebSocket(chunkSize: 10)
            .Text(new byte[65])
            .Text("next"u8.ToArray());
        var reader = new WebChatMessageReader(maxMessageBytes: 64);

        var oversized = await reader.ReadAsync(socket, CancellationToken.None);
        var next = await reader.ReadAsync(socket, CancellationToken.None);

        oversized.Kind.Should().Be(WebChatFrameKind.Oversized);
        oversized.Payload.IsEmpty.Should().BeTrue();
        next.Kind.Should().Be(WebChatFrameKind.Text, "the rest of the oversized message was drained, not read as the next one");
        Encoding.UTF8.GetString(next.Payload.Span).Should().Be("next");
    }

    [Fact]
    public async Task ReadAsync_ShouldIgnoreBinaryMessageAndReadNext_WhenBinarySpansSeveralReads()
    {
        var socket = new ScriptedWebSocket(chunkSize: 10)
            .Binary(new byte[100])
            .Text("after"u8.ToArray());
        var reader = new WebChatMessageReader(maxMessageBytes: 64);

        var binary = await reader.ReadAsync(socket, CancellationToken.None);
        var next = await reader.ReadAsync(socket, CancellationToken.None);

        binary.Kind.Should().Be(WebChatFrameKind.Ignored);
        next.Kind.Should().Be(WebChatFrameKind.Text);
        Encoding.UTF8.GetString(next.Payload.Span).Should().Be("after");
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnClose_WhenPeerCloses()
    {
        var socket = new ScriptedWebSocket(chunkSize: 10).Close();

        var frame = await new WebChatMessageReader(maxMessageBytes: 64).ReadAsync(socket, CancellationToken.None);

        frame.Kind.Should().Be(WebChatFrameKind.Close);
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnClose_WhenPeerClosesMidMessage()
    {
        var socket = new ScriptedWebSocket(chunkSize: 10).Partial("abc"u8.ToArray()).Close();

        var frame = await new WebChatMessageReader(maxMessageBytes: 64).ReadAsync(socket, CancellationToken.None);

        frame.Kind.Should().Be(WebChatFrameKind.Close);
    }

    [Fact]
    public void MaxMessageBytesFor_ShouldCoverSixBytesPerCharacterPlusEnvelope_WhenGivenALength()
    {
        // A character can take up to 6 bytes inside a JSON string (a \uXXXX escape), so a message of the full
        // allowed length always fits, whatever the client's encoder escapes.
        WebChatMessageReader.MaxMessageBytesFor(4000).Should().Be(4000 * 6 + WebChatMessageReader.EnvelopeAllowanceBytes);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenBoundIsNotPositive()
    {
        var act = () => new WebChatMessageReader(maxMessageBytes: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A peer that sends scripted messages, each handed out in reads of at most <c>chunkSize</c> bytes (and never
    /// more than the reader's buffer), with <c>EndOfMessage</c> on the last read of each.
    /// </summary>
    private sealed class ScriptedWebSocket(int chunkSize) : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Data, bool Ends)> _messages = new();
        private int _offset;
        private WebSocketState _state = WebSocketState.Open;

        public int Reads { get; private set; }

        public ScriptedWebSocket Text(byte[] data) => Enqueue(WebSocketMessageType.Text, data, ends: true);

        public ScriptedWebSocket Binary(byte[] data) => Enqueue(WebSocketMessageType.Binary, data, ends: true);

        public ScriptedWebSocket Partial(byte[] data) => Enqueue(WebSocketMessageType.Text, data, ends: false);

        public ScriptedWebSocket Close() => Enqueue(WebSocketMessageType.Close, [], ends: true);

        private ScriptedWebSocket Enqueue(WebSocketMessageType type, byte[] data, bool ends)
        {
            _messages.Enqueue((type, data, ends));
            return this;
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Reads++;
            var (type, data, ends) = _messages.Peek();
            if (type == WebSocketMessageType.Close)
            {
                _messages.Dequeue();
                _state = WebSocketState.CloseReceived;
                return ValueTask.FromResult(new ValueWebSocketReceiveResult(0, type, true));
            }

            var count = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _offset);
            data.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            var last = _offset == data.Length;
            if (last)
            {
                _messages.Dequeue();
                _offset = 0;
            }

            return ValueTask.FromResult(new ValueWebSocketReceiveResult(count, type, last && ends));
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
    }
}
