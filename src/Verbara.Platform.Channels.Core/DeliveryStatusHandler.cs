using System.Diagnostics.Metrics;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// Applies a provider's delivery-status callback to the outbound message it names (message-delivery-correlation).
/// Statuses only move forward (<see cref="MessageDeliveryStatusRules"/>): a replayed or late callback is ignored
/// here and, atomically, by the store's own guard. A callback for a provider id no message carries is logged and
/// counted on <c>channels.delivery_status.unknown_id</c>; it is not parked (design D8, open question).
/// </summary>
public sealed class DeliveryStatusHandler
{
    /// <summary>Name of the <see cref="Meter"/> carrying the channel delivery-status instruments.</summary>
    public const string MeterName = "verbara.platform.channels";

    private readonly IMessageStore _messageStore;
    private readonly ILogger<DeliveryStatusHandler> _logger;
    private readonly Counter<long> _unknownIds;

    public DeliveryStatusHandler(
        IMessageStore messageStore, ILogger<DeliveryStatusHandler> logger, IMeterFactory? meterFactory = null)
    {
        _messageStore = messageStore;
        _logger = logger;
        var meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        _unknownIds = meter.CreateCounter<long>(
            "channels.delivery_status.unknown_id",
            description: "Provider delivery-status callbacks whose message id matched no stored message.");
    }

    public async Task HandleAsync(TenantId tenantId, DeliveryStatusUpdate update, CancellationToken ct)
    {
        var message = await _messageStore
            .FindByExternalIdAsync(tenantId, update.ExternalMessageId, ct)
            .ConfigureAwait(false);

        if (message is null)
        {
            _unknownIds.Add(1);
            Log.UnknownExternalMessage(_logger, update.ExternalMessageId);
            return;
        }

        if (!MessageDeliveryStatusRules.CanAdvance(message.DeliveryStatus, update.NewStatus))
        {
            Log.StatusNotAdvanced(_logger, message.MessageId.Value, message.DeliveryStatus, update.NewStatus);
            return;
        }

        await _messageStore
            .UpdateDeliveryStatusAsync(tenantId, message.MessageId, update.NewStatus, update.Timestamp, ct)
            .ConfigureAwait(false);

        Log.StatusUpdated(_logger, message.MessageId.Value, update.NewStatus);
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Unknown external message ID '{ExternalMessageId}' — ignoring delivery status update.")]
    internal static partial void UnknownExternalMessage(ILogger logger, string externalMessageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivery status for message '{MessageId}' updated to '{Status}'.")]
    internal static partial void StatusUpdated(ILogger logger, string messageId, MessageDeliveryStatus status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivery status for message '{MessageId}' stays '{Current}'; '{Incoming}' would not move it forward.")]
    internal static partial void StatusNotAdvanced(ILogger logger, string messageId, MessageDeliveryStatus current, MessageDeliveryStatus incoming);
}
