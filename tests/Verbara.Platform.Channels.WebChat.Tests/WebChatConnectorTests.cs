using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

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
