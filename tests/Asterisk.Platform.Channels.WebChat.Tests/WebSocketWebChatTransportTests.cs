using System.Net.WebSockets;
using Asterisk.Platform.Conversations;
using NSubstitute;

namespace Asterisk.Platform.Channels.WebChat.Tests;

public class WebSocketWebChatTransportTests
{
    private static MessageEnvelope MakeEnvelope(string text = "Hello") =>
        new([new TextBlock(text)]);

    [Fact]
    public async Task SendToClientAsync_ShouldSendJson_WhenClientIsConnected()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        transport.Register("sess-1", ws);

        await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);

        await ws.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToClientAsync_ShouldBeNoOp_WhenClientIsNotConnected()
    {
        var transport = new WebSocketWebChatTransport();

        var act = () => transport.SendToClientAsync("nonexistent", MakeEnvelope(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisconnectAsync_ShouldCloseSocket_WhenOpen()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        transport.Register("sess-1", ws);

        await transport.DisconnectAsync("sess-1", CancellationToken.None);

        await ws.Received(1).CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Session ended",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Register_ShouldTrackConnection_AndUnregister_ShouldRemoveIt()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        transport.Register("sess-1", ws);
        transport.Unregister("sess-1");

        var task = transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);
        task.IsCompleted.Should().BeTrue();
    }
}
