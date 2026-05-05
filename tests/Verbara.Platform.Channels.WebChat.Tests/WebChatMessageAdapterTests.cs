using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat.Tests;

public class WebChatMessageAdapterTests
{
    private static MessageEnvelope MakeEnvelope() =>
        new(new List<MessageBlock>());

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectChannelAddress()
    {
        var sessionId = "session-xyz";
        var envelope = MakeEnvelope();
        var externalId = "ext-001";
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var result = WebChatMessageAdapter.ToInboundMessage(sessionId, envelope, externalId, timestamp);

        result.From.Channel.Should().Be(ChannelType.WebChat);
        result.From.Address.Should().Be(sessionId);
    }

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectContent()
    {
        var envelope = MakeEnvelope();

        var result = WebChatMessageAdapter.ToInboundMessage("session-1", envelope, "ext-id", DateTimeOffset.UtcNow);

        result.Content.Should().BeSameAs(envelope);
    }

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectExternalMessageId()
    {
        var externalId = "msg-external-123";

        var result = WebChatMessageAdapter.ToInboundMessage("session-1", MakeEnvelope(), externalId, DateTimeOffset.UtcNow);

        result.ExternalMessageId.Should().Be(externalId);
    }

    [Fact]
    public void ToInboundMessage_ShouldReturnInboundMessage_WithCorrectTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 6, 15, 9, 30, 0, TimeSpan.Zero);

        var result = WebChatMessageAdapter.ToInboundMessage("session-1", MakeEnvelope(), "ext-id", timestamp);

        result.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void ToInboundMessage_ShouldSetFromChannelToWebChat_ForAnySessionId()
    {
        var sessionIds = new[] { "abc", "def123", "visitor-999" };

        foreach (var sessionId in sessionIds)
        {
            var result = WebChatMessageAdapter.ToInboundMessage(sessionId, MakeEnvelope(), "ext-id", DateTimeOffset.UtcNow);
            result.From.Channel.Should().Be(ChannelType.WebChat);
            result.From.Address.Should().Be(sessionId);
        }
    }
}
