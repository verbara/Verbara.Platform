using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Video;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Channels.Video.Tests;

public class VideoConnectorTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly EntityId ConversationA = EntityId.From("conv-1");
    private static readonly EntityId AgentA = EntityId.From("agent-1");

    private static VideoSessionManager CreateManager(IVideoTransport transport) =>
        new(transport, Options.Create(new VideoOptions()));

    private static OutboundMessage MakeMessage(EntityId conversationId) =>
        new(
            To: new ChannelAddress(ChannelType.Video, conversationId.Value),
            Content: new MessageEnvelope(new List<MessageBlock>()),
            TenantId: TenantA,
            ConversationId: conversationId);

    private static VideoSession MakeSession(string sessionId = "session-1") =>
        new(sessionId, "wss://signaling.example.com", "cust-token", "agent-token", DateTimeOffset.UtcNow);

    [Fact]
    public void Channel_ShouldBeVideo()
    {
        var transport = Substitute.For<IVideoTransport>();
        var manager = CreateManager(transport);
        var connector = new VideoConnector(manager);

        connector.Channel.Should().Be(ChannelType.Video);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSuccess_WhenActiveSessionExists()
    {
        var transport = Substitute.For<IVideoTransport>();
        var session = MakeSession();
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(session);

        var manager = CreateManager(transport);
        await manager.CreateAsync(
            new VideoSessionRequest(TenantA, ConversationA, AgentA, "customer"),
            CancellationToken.None);

        var connector = new VideoConnector(manager);
        var result = await connector.SendAsync(MakeMessage(ConversationA), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenNoSessionExists()
    {
        var transport = Substitute.For<IVideoTransport>();
        var manager = CreateManager(transport);
        var connector = new VideoConnector(manager);
        var message = MakeMessage(EntityId.From("conv-no-session"));

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("SESSION_NOT_FOUND");
        result.ExternalMessageId.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReturnDelivered_WhenCalled()
    {
        var transport = Substitute.For<IVideoTransport>();
        var manager = CreateManager(transport);
        var connector = new VideoConnector(manager);

        var status = await connector.GetStatusAsync("any-message-id", CancellationToken.None);

        status.Should().Be(MessageDeliveryStatus.Delivered);
    }
}
