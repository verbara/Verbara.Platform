using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace Verbara.Platform.Channels.Core.Tests;

public sealed class DeliveryStatusHandlerTests : IDisposable
{
    private static readonly TenantId TenantId = new("tenant-1");

    private readonly IMessageStore _messageStore;
    private readonly DeliveryStatusHandler _handler;

    public DeliveryStatusHandlerTests()
    {
        _messageStore = Substitute.For<IMessageStore>();
        _handler = new DeliveryStatusHandler(_messageStore, NullLogger<DeliveryStatusHandler>.Instance);
    }

    public void Dispose() => _handler.Dispose();

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

    [Fact]
    public async Task HandleAsync_ShouldNotDowngrade_WhenSentArrivesAfterRead()
    {
        // Meta signs the body only (no timestamp), so a replayed or late 'sent' callback is routine.
        var externalId = "wa-msg-006";
        var message = MakeMessage(externalId, MessageDeliveryStatus.Read);
        var update = new DeliveryStatusUpdate(externalId, MessageDeliveryStatus.Sent, DateTimeOffset.UtcNow);

        _messageStore.FindByExternalIdAsync(TenantId, externalId, Arg.Any<CancellationToken>())
            .Returns(message);

        await _handler.HandleAsync(TenantId, update, CancellationToken.None);

        await _messageStore.DidNotReceive().UpdateDeliveryStatusAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<MessageDeliveryStatus>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldCountUnknownId_WhenNoMessageMatches()
    {
        using var meterFactory = new TestMeterFactory();
        var handler = new DeliveryStatusHandler(_messageStore, NullLogger<DeliveryStatusHandler>.Instance, meterFactory);
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (meterFactory.Owns(instrument.Meter) && instrument.Name == "channels.delivery_status.unknown_id")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();
        _messageStore.FindByExternalIdAsync(TenantId, "never-sent", Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.HandleAsync(TenantId, new DeliveryStatusUpdate("never-sent", MessageDeliveryStatus.Delivered, DateTimeOffset.UtcNow), CancellationToken.None);

        counted.Should().Be(1);
    }

    [Theory]
    [InlineData(MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Sent)]
    [InlineData(MessageDeliveryStatus.Failed, MessageDeliveryStatus.Delivered)]
    [InlineData(MessageDeliveryStatus.Read, MessageDeliveryStatus.Read)]
    public async Task HandleAsync_ShouldNotWrite_WhenStatusWouldNotMoveForward(
        MessageDeliveryStatus current, MessageDeliveryStatus incoming)
    {
        var message = MakeMessage("wa-msg-007", current);
        _messageStore.FindByExternalIdAsync(TenantId, "wa-msg-007", Arg.Any<CancellationToken>()).Returns(message);

        await _handler.HandleAsync(TenantId, new DeliveryStatusUpdate("wa-msg-007", incoming, DateTimeOffset.UtcNow), CancellationToken.None);

        await _messageStore.DidNotReceive().UpdateDeliveryStatusAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<MessageDeliveryStatus>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public bool Owns(Meter meter) => _meters.Contains(meter);

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}
