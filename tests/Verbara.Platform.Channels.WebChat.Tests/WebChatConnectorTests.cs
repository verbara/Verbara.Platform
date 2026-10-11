using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Channels.WebChat.Tests;

public class WebChatConnectorTests
{
    private static WebChatSessionManager CreateManager(WebChatOptions? opts = null)
    {
        var options = opts ?? new WebChatOptions();
        return new WebChatSessionManager(Options.Create(options));
    }

    private static MessageEnvelope MakeEnvelope() =>
        new(new List<MessageBlock>());

    private const string VisitorAddress = "webchat-7c9e6679f2a54d0b8e1a3b5c7d9f1e2a";

    private static OutboundMessage MakeMessage(string address) =>
        new(
            To: new ChannelAddress(ChannelType.WebChat, address),
            Content: MakeEnvelope(),
            TenantId: new TenantId("tenant-1"),
            ConversationId: EntityId.From("conv-1"));

    [Fact]
    public async Task SendAsync_ShouldDelegateToTransport_WhenSessionIsConnected()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var manager = CreateManager();
        var sessionId = await manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, VisitorAddress),
            clock);

        var transport = Substitute.For<IWebChatTransport>();
        transport.SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>()).Returns(true);
        var connector = new WebChatConnector(manager, transport);
        var message = MakeMessage(VisitorAddress);

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().NotBeNullOrEmpty();
        await transport.Received(1).SendToClientAsync(sessionId, message.Content, CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenSessionIsNotConnected()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var manager = CreateManager();
        var sessionId = await manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, VisitorAddress),
            clock);
        await manager.DisconnectAsync(sessionId);

        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);
        var message = MakeMessage(VisitorAddress);

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("SESSION_NOT_CONNECTED");
        await transport.DidNotReceive().SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenSessionDoesNotExist()
    {
        var manager = CreateManager();
        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);
        var message = MakeMessage("webchat-nobody");

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("SESSION_NOT_CONNECTED");
        await transport.DidNotReceive().SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenAddressIsTheSessionId()
    {
        // The session id is a transport handle, never a contact address (D16): addressing a reply to it
        // finds no visitor and the send fails instead of reaching the socket.
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var manager = CreateManager();
        var sessionId = await manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, VisitorAddress),
            clock);

        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);

        var result = await connector.SendAsync(MakeMessage(sessionId), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WebChatConnector.SessionNotConnected);
        await transport.DidNotReceive().SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenVisitorSessionExpired()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(start);
        var manager = CreateManager(new WebChatOptions { SessionTimeout = TimeSpan.FromMinutes(30) });
        await manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, VisitorAddress),
            clock);
        clock.UtcNow.Returns(start.AddMinutes(31));
        await manager.CleanupExpiredAsync(clock);

        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);

        var result = await connector.SendAsync(MakeMessage(VisitorAddress), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WebChatConnector.SessionNotConnected);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenNoSocketTookReply()
    {
        // The session exists but no visitor socket is attached yet (or every socket just closed): the
        // transport reports that nothing took the frame, and the send is a failure, never a false Sent.
        var manager = CreateManager();
        await ConnectVisitorAsync(manager);
        var transport = Substitute.For<IWebChatTransport>();
        transport.SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>()).Returns(false);
        var connector = new WebChatConnector(manager, transport);

        var result = await connector.SendAsync(MakeMessage(VisitorAddress), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ExternalMessageId.Should().BeNull();
        result.ErrorCode.Should().Be(WebChatConnector.SessionNotConnected);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenReplyHandledByAnotherReplica()
    {
        // Replica A holds the visitor's session; replica B has its own in-process manager and never saw it.
        var replicaA = CreateManager();
        await ConnectVisitorAsync(replicaA);
        var replicaB = CreateManager();
        var transportB = Substitute.For<IWebChatTransport>();
        var connectorB = new WebChatConnector(replicaB, transportB);

        var result = await connectorB.SendAsync(MakeMessage(VisitorAddress), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WebChatConnector.SessionNotConnected);
        await transportB.DidNotReceive().SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldPropagateCancellation_WhenCallerCancels()
    {
        var manager = CreateManager();
        await ConnectVisitorAsync(manager);
        var transport = Substitute.For<IWebChatTransport>();
        transport.SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var connector = new WebChatConnector(manager, transport);

        var send = () => connector.SendAsync(MakeMessage(VisitorAddress), CancellationToken.None);

        await send.Should().ThrowAsync<OperationCanceledException>("cancellation is the caller's, not a delivery failure");
    }

    [Theory]
    [InlineData("socket")]
    [InlineData("concurrent")]
    [InlineData("disposed")]
    public async Task SendAsync_ShouldReturnFailure_WhenTransportThrows(string failure)
    {
        // The socket can close between the state check and the send, or a second send can race the
        // first: the spec records that as a failed send, never as an exception reaching the agent.
        Exception thrown = failure switch
        {
            "socket" => new WebSocketException("connection reset"),
            "concurrent" => new InvalidOperationException("There is already one outstanding 'SendAsync' call."),
            _ => new ObjectDisposedException("socket"),
        };
        var manager = CreateManager();
        await ConnectVisitorAsync(manager);
        var transport = Substitute.For<IWebChatTransport>();
        transport.SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);
        var connector = new WebChatConnector(manager, transport);

        var send = () => connector.SendAsync(MakeMessage(VisitorAddress), CancellationToken.None);

        var result = (await send.Should().NotThrowAsync()).Subject;
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WebChatConnector.SessionNotConnected);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenSessionBelongsToAnotherTenant()
    {
        var manager = CreateManager();
        await ConnectVisitorAsync(manager); // tenant-1's visitor
        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);
        var fromOtherTenant = MakeMessage(VisitorAddress) with { TenantId = new TenantId("tenant-2") };

        var result = await connector.SendAsync(fromOtherTenant, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WebChatConnector.SessionNotConnected);
        await transport.DidNotReceive().SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
    }

    private static Task<string> ConnectVisitorAsync(WebChatSessionManager manager)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        return manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, VisitorAddress),
            clock);
    }

    [Fact]
    public void Channel_ShouldBeWebChat()
    {
        var manager = CreateManager();
        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);

        connector.Channel.Should().Be(ChannelType.WebChat);
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReturnDelivered_WhenCalled()
    {
        var manager = CreateManager();
        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);

        var status = await connector.GetStatusAsync("any-message-id", CancellationToken.None);

        status.Should().Be(MessageDeliveryStatus.Delivered);
    }
}
