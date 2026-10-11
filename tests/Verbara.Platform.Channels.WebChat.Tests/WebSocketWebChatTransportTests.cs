using System.Net.WebSockets;
using Verbara.Platform.Conversations;
using NSubstitute;

namespace Verbara.Platform.Channels.WebChat.Tests;

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
    public async Task Unregister_ShouldStopDelivery_WhenLastSocketRemoved()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = OpenSocket();

        transport.Register("sess-1", ws);
        transport.Unregister("sess-1", ws);

        var delivered = await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);
        delivered.Should().BeFalse();
        await ws.DidNotReceiveWithAnyArgs().SendAsync(default(ArraySegment<byte>), default, default, default);
    }

    [Fact]
    public async Task SendToClientAsync_ShouldReturnTrue_WhenSocketTookFrame()
    {
        var transport = new WebSocketWebChatTransport();
        transport.Register("sess-1", OpenSocket());

        var delivered = await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);

        delivered.Should().BeTrue();
    }

    [Fact]
    public async Task SendToClientAsync_ShouldReturnFalse_WhenNoSocketRegistered()
    {
        var transport = new WebSocketWebChatTransport();

        var delivered = await transport.SendToClientAsync("never-attached", MakeEnvelope(), CancellationToken.None);

        delivered.Should().BeFalse();
    }

    [Fact]
    public async Task SendToClientAsync_ShouldReturnFalse_WhenSocketNotOpen()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.CloseReceived);
        transport.Register("sess-1", ws);

        var delivered = await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);

        delivered.Should().BeFalse();
        await ws.DidNotReceiveWithAnyArgs().SendAsync(default(ArraySegment<byte>), default, default, default);
    }

    [Fact]
    public async Task SendToClientAsync_ShouldReturnFalse_WhenSocketSendThrows()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = OpenSocket();
        ws.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new WebSocketException("connection reset")));
        transport.Register("sess-1", ws);

        var delivered = await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);

        delivered.Should().BeFalse();
    }

    [Fact]
    public async Task Unregister_ShouldKeepOtherSocket_WhenSessionHasTwoSockets()
    {
        // Two tabs (or a reload that re-attached before the old socket's cleanup): removing one socket
        // must not remove the other.
        var transport = new WebSocketWebChatTransport();
        var stays = OpenSocket();
        var leaves = OpenSocket();
        transport.Register("sess-1", stays);
        transport.Register("sess-1", leaves);

        transport.Unregister("sess-1", leaves);
        var delivered = await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);

        delivered.Should().BeTrue();
        await stays.Received(1).SendAsync(Arg.Any<ArraySegment<byte>>(), WebSocketMessageType.Text, true, Arg.Any<CancellationToken>());
        await leaves.DidNotReceiveWithAnyArgs().SendAsync(default(ArraySegment<byte>), default, default, default);
    }

    [Fact]
    public async Task SendToClientAsync_ShouldSerializeSends_WhenTwoRepliesRace()
    {
        // A WebSocket allows one outstanding send: the second reply waits for the first to complete.
        var transport = new WebSocketWebChatTransport();
        var ws = OpenSocket();
        var firstSend = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;
        var calls = 0;
        ws.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var now = Interlocked.Increment(ref inFlight);
                maxInFlight = Math.Max(maxInFlight, now);
                var isFirst = Interlocked.Increment(ref calls) == 1;
                if (isFirst)
                    firstStarted.TrySetResult();
                var task = isFirst ? firstSend.Task : Task.CompletedTask;
                return task.ContinueWith(
                    _ => Interlocked.Decrement(ref inFlight),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            });
        transport.Register("sess-1", ws);

        var first = transport.SendToClientAsync("sess-1", MakeEnvelope("one"), CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = transport.SendToClientAsync("sess-1", MakeEnvelope("two"), CancellationToken.None);
        Volatile.Read(ref calls).Should().Be(1, "the second send is queued behind the first, still in flight");
        firstSend.SetResult();

        (await first).Should().BeTrue();
        (await second).Should().BeTrue();
        calls.Should().Be(2);
        maxInFlight.Should().Be(1);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldDoNothing_WhenSessionUnknown()
    {
        var transport = new WebSocketWebChatTransport();

        var act = () => transport.DisconnectAsync("unknown", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static WebSocket OpenSocket()
    {
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        return ws;
    }
}
