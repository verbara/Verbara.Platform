using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Channels.Core;

public sealed class DeliveryStatusHandler
{
    private readonly IMessageStore _messageStore;
    private readonly ILogger<DeliveryStatusHandler> _logger;

    public DeliveryStatusHandler(IMessageStore messageStore, ILogger<DeliveryStatusHandler> logger)
    {
        _messageStore = messageStore;
        _logger = logger;
    }

    public async Task HandleAsync(TenantId tenantId, DeliveryStatusUpdate update, CancellationToken ct)
    {
        var message = await _messageStore
            .FindByExternalIdAsync(tenantId, update.ExternalMessageId, ct)
            .ConfigureAwait(false);

        if (message is null)
        {
            Log.UnknownExternalMessage(_logger, update.ExternalMessageId);
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
    [LoggerMessage(Level = LogLevel.Debug, Message = "Unknown external message ID '{ExternalMessageId}' — ignoring delivery status update.")]
    internal static partial void UnknownExternalMessage(ILogger logger, string externalMessageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivery status for message '{MessageId}' updated to '{Status}'.")]
    internal static partial void StatusUpdated(ILogger logger, string messageId, MessageDeliveryStatus status);
}
