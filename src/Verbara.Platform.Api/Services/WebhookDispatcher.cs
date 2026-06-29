using System.Text.Json;
using System.Threading.Channels;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Api.Services;

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
    private Task? _lastDispatch;

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

    /// <summary>
    /// The most recently started dispatch Task. Test-only seam: lets tests await the
    /// detached SaveAsync+channel-write continuation deterministically instead of sleeping.
    /// HandleEventAsync swallows its own faults, so this Task completes normally even on error.
    /// </summary>
    internal Task? LastDispatch => _lastDispatch;

    /// <summary>
    /// Test-only seam: awaits the most recent dispatch (or a completed Task if none has run)
    /// against the caller's bounded timeout token. Production never calls this.
    /// </summary>
    internal Task WaitForDispatchAsync(CancellationToken cancellationToken) =>
        (_lastDispatch ?? Task.CompletedTask).WaitAsync(cancellationToken);

    // Fire-and-forget shim: Subject.OnNext is synchronous, so the field is assigned during
    // OnNext and the SaveAsync+channel-write continuation runs detached after the first await.
    // Production semantics are unchanged; only the started Task is now recorded for tests.
    private void OnEvent(PlatformEvent evt) => _lastDispatch = HandleEventAsync(evt);

    private async Task HandleEventAsync(PlatformEvent evt)
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
