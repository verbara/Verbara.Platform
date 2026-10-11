using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat.Tests;

public class WebChatMessageAdapterTests
{
    private static MessageEnvelope MakeEnvelope() =>
        new(new List<MessageBlock>());

    private static WebChatSession MakeSession(string sessionId = "session-1", string visitorAddress = "webchat-visitor-1") =>
        new()
        {
            SessionId = sessionId,
            ConversationId = EntityId.From("conv-1"),
            TenantId = new TenantId("tenant-1"),
            CustomerAddress = new ChannelAddress(ChannelType.WebChat, visitorAddress),
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectChannelAddress()
    {
        var session = MakeSession("session-xyz", "webchat-xyz");
        var envelope = MakeEnvelope();
        var externalId = "ext-001";
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var result = WebChatMessageAdapter.ToInboundMessage(session, envelope, externalId, timestamp);

        result.From.Channel.Should().Be(ChannelType.WebChat);
        result.From.Address.Should().Be("webchat-xyz");
    }

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectContent()
    {
        var envelope = MakeEnvelope();

        var result = WebChatMessageAdapter.ToInboundMessage(MakeSession(), envelope, "ext-id", DateTimeOffset.UtcNow);

        result.Content.Should().BeSameAs(envelope);
    }

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectExternalMessageId()
    {
        var externalId = "msg-external-123";

        var result = WebChatMessageAdapter.ToInboundMessage(MakeSession(), MakeEnvelope(), externalId, DateTimeOffset.UtcNow);

        result.ExternalMessageId.Should().Be(externalId);
    }

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 6, 15, 9, 30, 0, TimeSpan.Zero);

        var result = WebChatMessageAdapter.ToInboundMessage(MakeSession(), MakeEnvelope(), "ext-id", timestamp);

        result.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void ToInboundMessage_ShouldSetFromChannelToWebChat_ForAnyVisitorAddress()
    {
        var visitorAddresses = new[] { "webchat-abc", "webchat-def123", "webchat-999" };

        foreach (var visitorAddress in visitorAddresses)
        {
            var result = WebChatMessageAdapter.ToInboundMessage(
                MakeSession(visitorAddress: visitorAddress), MakeEnvelope(), "ext-id", DateTimeOffset.UtcNow);
            result.From.Channel.Should().Be(ChannelType.WebChat);
            result.From.Address.Should().Be(visitorAddress);
        }
    }

    [Fact]
    public void ToInboundMessage_ShouldUseVisitorAddress_NotSessionId()
    {
        // whatsapp-works-for-real D16: the session id is a transport handle; the contact address of a
        // WebChat visitor is the webchat-<guid> visitor address the session was created with.
        var session = new WebChatSession
        {
            SessionId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
            ConversationId = EntityId.From("conv-visitor-1"),
            TenantId = new TenantId("t-visitor"),
            CustomerAddress = new ChannelAddress(ChannelType.WebChat, "webchat-7c9e6679f2a54d0b8e1a3b5c7d9f1e2a"),
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };

        var result = WebChatMessageAdapter.ToInboundMessage(session, MakeEnvelope(), "ext-id", DateTimeOffset.UtcNow);

        result.From.Channel.Should().Be(ChannelType.WebChat);
        result.From.Address.Should().Be(session.CustomerAddress.Address);
        result.From.Address.Should().NotBe(session.SessionId);
    }
}
