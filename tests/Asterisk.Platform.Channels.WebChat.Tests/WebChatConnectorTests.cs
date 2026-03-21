using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.WebChat;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Channels.WebChat.Tests;

public class WebChatConnectorTests
{
    private static WebChatSessionManager CreateManager(WebChatOptions? opts = null)
    {
        var options = opts ?? new WebChatOptions();
        return new WebChatSessionManager(Options.Create(options));
    }

    private static MessageEnvelope MakeEnvelope() =>
        new(new List<MessageBlock>());

    private static OutboundMessage MakeMessage(string sessionId) =>
        new(
            To: new ChannelAddress(ChannelType.WebChat, sessionId),
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
            new ChannelAddress(ChannelType.WebChat, "visitor-abc"),
            clock);

        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);
        var message = MakeMessage(sessionId);

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
            new ChannelAddress(ChannelType.WebChat, "visitor-abc"),
            clock);
        await manager.DisconnectAsync(sessionId);

        var transport = Substitute.For<IWebChatTransport>();
        var connector = new WebChatConnector(manager, transport);
        var message = MakeMessage(sessionId);

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
        var message = MakeMessage("nonexistent-session-id");

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("SESSION_NOT_CONNECTED");
        await transport.DidNotReceive().SendToClientAsync(Arg.Any<string>(), Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
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
