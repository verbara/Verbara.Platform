using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Subscribes to PlatformEventBus and creates WebhookDelivery records for matching subscriptions.
/// Enqueues new deliveries into a Channel for immediate processing by WebhookDeliveryService.
/// </summary>
internal sealed partial class WebhookDispatcher : IDisposable
{
    private readonly IWebhookSubscriptionStore _subscriptionStore;
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IClock _clock;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly Channel<WebhookDelivery> _channel;
    private IDisposable? _subscription;

    public WebhookDispatcher(
        PlatformEventBus eventBus,
        IWebhookSubscriptionStore subscriptionStore,
        IWebhookDeliveryStore deliveryStore,
        IClock clock,
        ILogger<WebhookDispatcher> logger)
    {
        _subscriptionStore = subscriptionStore;
        _deliveryStore = deliveryStore;
        _clock = clock;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WebhookDelivery>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        _subscription = eventBus.Events.Subscribe(OnEvent);
    }

    /// <summary>
    /// Channel reader for WebhookDeliveryService to consume new deliveries.
    /// </summary>
    public ChannelReader<WebhookDelivery> DeliveryReader => _channel.Reader;

    private async void OnEvent(PlatformEvent evt)
    {
        try
        {
            var subs = await _subscriptionStore.GetActiveByEventTypeAsync(
                evt.TenantId, evt.Type, CancellationToken.None);

            if (subs.Count == 0)
                return;

            var payload = SerializePayload(evt);

            foreach (var sub in subs)
            {
                var now = _clock.UtcNow;
                var delivery = new WebhookDelivery(
                    DeliveryId: Guid.NewGuid().ToString("N"),
                    TenantId: evt.TenantId,
                    SubscriptionId: sub.SubscriptionId,
                    EventType: evt.Type,
                    Payload: payload,
                    Status: WebhookDeliveryStatus.Pending,
                    Attempts: 0,
                    MaxAttempts: 8,
                    NextRetryAt: now,
                    LastResponseCode: null,
                    LastError: null,
                    CreatedAt: now,
                    DeliveredAt: null);

                await _deliveryStore.SaveAsync(delivery, CancellationToken.None);
                await _channel.Writer.WriteAsync(delivery, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogDispatchError(_logger, evt.Type, evt.TenantId, ex);
        }
    }

    private static string SerializePayload(PlatformEvent evt)
    {
        var envelope = new WebhookEventPayload(
            Id: Guid.NewGuid().ToString("N"),
            Type: evt.Type,
            TenantId: evt.TenantId,
            Timestamp: evt.Timestamp,
            Data: evt);

        return JsonSerializer.Serialize(envelope, ApiJsonContext.Default.WebhookEventPayload);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _channel.Writer.TryComplete();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to dispatch webhook for event {EventType} in tenant {TenantId}")]
    private static partial void LogDispatchError(ILogger logger, string eventType, string tenantId, Exception ex);
}
