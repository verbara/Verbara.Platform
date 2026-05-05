using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Channels.Core.Tests;

public class DeliveryStatusHandlerTests
{
    private static readonly TenantId TenantId = new("tenant-1");

    private readonly IMessageStore _messageStore;
    private readonly DeliveryStatusHandler _handler;

    public DeliveryStatusHandlerTests()
    {
        _messageStore = Substitute.For<IMessageStore>();
        _handler = new DeliveryStatusHandler(_messageStore, NullLogger<DeliveryStatusHandler>.Instance);
    }

    private static Message MakeMessage(string externalId, MessageDeliveryStatus status = MessageDeliveryStatus.Pending) =>
        new()
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.New(),
            TenantId = TenantId,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([]),
            DeliveryStatus = status,
            ExternalMessageId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task HandleAsync_ShouldUpdateStatus_WhenMessageExists()
    {
        var externalId = "wa-msg-001";
        var message = MakeMessage(externalId, MessageDeliveryStatus.Pending);
        var update = new DeliveryStatusUpdate(externalId, MessageDeliveryStatus.Sent, DateTimeOffset.UtcNow);

        _messageStore.FindByExternalIdAsync(TenantId, externalId, Arg.Any<CancellationToken>())
            .Returns(message);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            TenantId,
            message.MessageId,
            MessageDeliveryStatus.Sent,
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldIgnore_WhenExternalMessageIdUnknown()
    {
        var update = new DeliveryStatusUpdate("unknown-ext-id", MessageDeliveryStatus.Delivered, DateTimeOffset.UtcNow);

        _messageStore.FindByExternalIdAsync(TenantId, "unknown-ext-id", Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.DidNotReceive().UpdateDeliveryStatusAsync(
            Arg.Any<TenantId>(),
            Arg.Any<EntityId>(),
            Arg.Any<MessageDeliveryStatus>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldTransitionPendingToSent_WhenStatusUpdateReceived()
    {
        var externalId = "wa-msg-002";
        var message = MakeMessage(externalId, MessageDeliveryStatus.Pending);
        var update = new DeliveryStatusUpdate(externalId, MessageDeliveryStatus.Sent, DateTimeOffset.UtcNow);

        _messageStore.FindByExternalIdAsync(TenantId, externalId, Arg.Any<CancellationToken>())
            .Returns(message);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            TenantId, message.MessageId, MessageDeliveryStatus.Sent,
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldTransitionSentToDelivered_WhenStatusUpdateReceived()
    {
        var externalId = "wa-msg-003";
        var message = MakeMessage(externalId, MessageDeliveryStatus.Sent);
        var update = new DeliveryStatusUpdate(externalId, MessageDeliveryStatus.Delivered, DateTimeOffset.UtcNow);

        _messageStore.FindByExternalIdAsync(TenantId, externalId, Arg.Any<CancellationToken>())
            .Returns(message);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            TenantId, message.MessageId, MessageDeliveryStatus.Delivered,
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldTransitionDeliveredToRead_WhenStatusUpdateReceived()
    {
        var externalId = "wa-msg-004";
        var message = MakeMessage(externalId, MessageDeliveryStatus.Delivered);
        var update = new DeliveryStatusUpdate(externalId, MessageDeliveryStatus.Read, DateTimeOffset.UtcNow);

        _messageStore.FindByExternalIdAsync(TenantId, externalId, Arg.Any<CancellationToken>())
            .Returns(message);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            TenantId, message.MessageId, MessageDeliveryStatus.Read,
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldPassTimestamp_WhenUpdatingStatus()
    {
        var externalId = "wa-msg-005";
        var timestamp = new DateTimeOffset(2026, 3, 21, 10, 0, 0, TimeSpan.Zero);
        var message = MakeMessage(externalId, MessageDeliveryStatus.Pending);
        var update = new DeliveryStatusUpdate(externalId, MessageDeliveryStatus.Delivered, timestamp);

        _messageStore.FindByExternalIdAsync(TenantId, externalId, Arg.Any<CancellationToken>())
            .Returns(message);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            TenantId, message.MessageId, MessageDeliveryStatus.Delivered,
            timestamp, Arg.Any<CancellationToken>());
    }
}
