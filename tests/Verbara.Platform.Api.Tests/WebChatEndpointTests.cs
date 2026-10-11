using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public class WebChatEndpointTests
{
    private static WebChatSessionManager CreateManager() =>
        new(Options.Create(new WebChatOptions()));

    [Fact]
    public async Task SessionManager_ShouldCreateSession_WithValidTenantId()
    {
        var manager = CreateManager();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var sessionId = await manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, "visitor-1"),
            clock);

        sessionId.Should().NotBeNullOrEmpty();
        var session = await manager.GetSessionAsync(sessionId);
        session.Should().NotBeNull();
        session!.TenantId.Value.Should().Be("tenant-1");
        session.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task SessionManager_ShouldRejectReconnect_WhenSessionExpired()
    {
        var manager = new WebChatSessionManager(Options.Create(new WebChatOptions
        {
            SessionTimeout = TimeSpan.FromMinutes(1)
        }));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var sessionId = await manager.ConnectAsync(
            new TenantId("t1"), EntityId.From("c1"),
            new ChannelAddress(ChannelType.WebChat, "v1"), clock);
        await manager.DisconnectAsync(sessionId);

        // Advance time past timeout
        clock.UtcNow.Returns(DateTimeOffset.UtcNow.AddMinutes(5));

        var reconnected = await manager.ReconnectAsync(sessionId, clock);
        reconnected.Should().BeFalse();
    }

    [Fact]
    public async Task Transport_ShouldSend_WhenRegisteredAndOpen()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<System.Net.WebSockets.WebSocket>();
        ws.State.Returns(System.Net.WebSockets.WebSocketState.Open);
        transport.Register("sess-1", ws);

        await transport.SendToClientAsync(
            "sess-1", new MessageEnvelope([new TextBlock("Hi")]), CancellationToken.None);

        await ws.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            System.Net.WebSockets.WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void WebChatMessageAdapter_ShouldCreateInboundMessage_WithCorrectChannel()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello")]);
        var session = new WebChatSession
        {
            SessionId = "sess-1",
            ConversationId = EntityId.From("conv-1"),
            TenantId = new TenantId("tenant-1"),
            CustomerAddress = new ChannelAddress(ChannelType.WebChat, "webchat-visitor-1"),
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
        var msg = WebChatMessageAdapter.ToInboundMessage(
            session, envelope, "ext-1", DateTimeOffset.UtcNow);

        msg.From.Channel.Should().Be(ChannelType.WebChat);
        msg.From.Address.Should().Be("webchat-visitor-1");
        msg.ExternalMessageId.Should().Be("ext-1");
    }

    [Fact]
    public async Task Transport_ShouldBeNoOp_WhenSessionNotRegistered()
    {
        var transport = new WebSocketWebChatTransport();

        var act = () => transport.SendToClientAsync(
            "unknown", new MessageEnvelope([new TextBlock("Hi")]), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
