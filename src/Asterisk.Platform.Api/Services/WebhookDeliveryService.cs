using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Background service that delivers webhook HTTP POST requests with retry and dead-letter support.
/// Reads new deliveries from WebhookDispatcher's Channel and polls the store for pending retries.
/// </summary>
internal sealed partial class WebhookDeliveryService : BackgroundService
{
    private static readonly int[] BackoffSeconds = [0, 60, 300, 1800, 7200, 18000, 28800, 28800];
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly WebhookDispatcher _dispatcher;
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IWebhookSubscriptionStore _subscriptionStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CircuitBreakerPolicy _circuitBreaker;
    private readonly IClock _clock;
    private readonly ILogger<WebhookDeliveryService> _logger;

    public WebhookDeliveryService(
        WebhookDispatcher dispatcher,
        IWebhookDeliveryStore deliveryStore,
        IWebhookSubscriptionStore subscriptionStore,
        IHttpClientFactory httpClientFactory,
        CircuitBreakerPolicy circuitBreaker,
        IClock clock,
        ILogger<WebhookDeliveryService> logger)
    {
        _dispatcher = dispatcher;
        _deliveryStore = deliveryStore;
        _subscriptionStore = subscriptionStore;
        _httpClientFactory = httpClientFactory;
        _circuitBreaker = circuitBreaker;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run both loops concurrently: channel reader + DB poller
        var channelTask = ProcessChannelAsync(stoppingToken);
        var pollTask = PollPendingRetriesAsync(stoppingToken);

        await Task.WhenAll(channelTask, pollTask);
    }

    private async Task ProcessChannelAsync(CancellationToken ct)
    {
        await foreach (var delivery in _dispatcher.DeliveryReader.ReadAllAsync(ct))
        {
            await DeliverAsync(delivery, ct);
        }
    }

    private async Task PollPendingRetriesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryPollInterval, ct);

                var pending = await _deliveryStore.ListPendingRetriesAsync(
                    _clock.UtcNow, batchSize: 100, ct);

                foreach (var delivery in pending)
                {
                    if (ct.IsCancellationRequested) break;
                    await DeliverAsync(delivery, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPollError(_logger, ex);
            }
        }
    }

    private async Task DeliverAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        try
        {
            var sub = await _subscriptionStore.GetByIdAsync(delivery.SubscriptionId, ct);
            if (sub is null)
            {
                // Subscription deleted — mark as dead letter
                var orphaned = delivery with
                {
                    Status = WebhookDeliveryStatus.DeadLetter,
                    LastError = "Subscription deleted",
                };
                await _deliveryStore.UpdateAsync(orphaned, ct);
                return;
            }

            // Transition Open→HalfOpen if cooldown has expired
            var now = _clock.UtcNow;
            var transitioned = CircuitBreakerPolicy.TransitionIfCooldownExpired(sub, now);
            if (!ReferenceEquals(transitioned, sub))
            {
                await _subscriptionStore.SaveAsync(transitioned, ct);
                sub = transitioned;
            }

            // Check circuit breaker — skip delivery if circuit is open
            if (!CircuitBreakerPolicy.ShouldDeliver(sub, now))
            {
                LogCircuitSkipped(_logger, delivery.DeliveryId, delivery.SubscriptionId);
                return;
            }

            var client = _httpClientFactory.CreateClient("webhooks");
            client.Timeout = HttpTimeout;

            var timestamp = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var signature = WebhookSignatureService.ComputeSignature(timestamp, delivery.Payload, sub.Secret);

            using var request = new HttpRequestMessage(HttpMethod.Post, sub.EndpointUrl);
            request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Webhook-Id", delivery.DeliveryId);
            request.Headers.Add("X-Webhook-Event", delivery.EventType);
            request.Headers.Add("X-Webhook-Timestamp", timestamp);
            request.Headers.Add("X-Webhook-Signature", signature);

            using var response = await client.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                var updatedSub = CircuitBreakerPolicy.OnSuccess(sub);
                if (updatedSub != sub)
                {
                    await _subscriptionStore.SaveAsync(updatedSub, ct);
                    LogCircuitRecovered(_logger, delivery.SubscriptionId);
                }

                var delivered = delivery with
                {
                    Status = WebhookDeliveryStatus.Delivered,
                    Attempts = delivery.Attempts + 1,
                    LastResponseCode = statusCode,
                    LastError = null,
                    NextRetryAt = null,
                    DeliveredAt = _clock.UtcNow,
                };
                await _deliveryStore.UpdateAsync(delivered, ct);
                LogDeliverySuccess(_logger, delivery.DeliveryId, sub.EndpointUrl, statusCode);
            }
            else
            {
                await HandleFailureAsync(delivery, sub, statusCode, $"HTTP {statusCode}", ct);
            }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — leave delivery in current state for next startup
        }
        catch (TaskCanceledException)
        {
            // HTTP timeout — need subscription for circuit breaker; re-fetch if possible
            var sub = await TryGetSubscriptionAsync(delivery.SubscriptionId, ct);
            await HandleFailureAsync(delivery, sub, null, "Request timeout", ct);
        }
        catch (HttpRequestException ex)
        {
            var sub = await TryGetSubscriptionAsync(delivery.SubscriptionId, ct);
            await HandleFailureAsync(delivery, sub, null, $"Network error: {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            LogDeliveryError(_logger, delivery.DeliveryId, ex);
            var sub = await TryGetSubscriptionAsync(delivery.SubscriptionId, ct);
            await HandleFailureAsync(delivery, sub, null, $"Unexpected error: {ex.Message}", ct);
        }
    }

    private async Task<WebhookSubscription?> TryGetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        try { return await _subscriptionStore.GetByIdAsync(subscriptionId, ct); }
        catch { return null; }
    }

    private async Task HandleFailureAsync(
        WebhookDelivery delivery, WebhookSubscription? sub, int? responseCode, string error, CancellationToken ct)
    {
        // Update circuit breaker state on the subscription
        if (sub is not null)
        {
            var (updatedSub, justOpened) = _circuitBreaker.OnFailure(sub, _clock.UtcNow);
            if (updatedSub != sub)
            {
                await _subscriptionStore.SaveAsync(updatedSub, ct);
                if (justOpened)
                    LogCircuitOpened(_logger, delivery.SubscriptionId, updatedSub.CircuitFailures);
            }
        }

        var newAttempts = delivery.Attempts + 1;

        if (newAttempts >= delivery.MaxAttempts)
        {
            var deadLetter = delivery with
            {
                Status = WebhookDeliveryStatus.DeadLetter,
                Attempts = newAttempts,
                LastResponseCode = responseCode,
                LastError = error,
                NextRetryAt = null,
            };
            await _deliveryStore.UpdateAsync(deadLetter, ct);
            LogDeadLetter(_logger, delivery.DeliveryId, newAttempts);
        }
        else
        {
            var backoffIndex = Math.Min(newAttempts, BackoffSeconds.Length - 1);
            var nextRetry = _clock.UtcNow.AddSeconds(BackoffSeconds[backoffIndex]);

            var retry = delivery with
            {
                Status = WebhookDeliveryStatus.Pending,
                Attempts = newAttempts,
                LastResponseCode = responseCode,
                LastError = error,
                NextRetryAt = nextRetry,
            };
            await _deliveryStore.UpdateAsync(retry, ct);
            LogRetryScheduled(_logger, delivery.DeliveryId, newAttempts, nextRetry);
        }
    }

    /// <summary>
    /// Backoff schedule for external consumers (e.g., tests).
    /// </summary>
    internal static int GetBackoffSeconds(int attemptNumber)
    {
        var index = Math.Min(attemptNumber, BackoffSeconds.Length - 1);
        return BackoffSeconds[index];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit breaker opened for subscription {SubscriptionId} after {Failures} consecutive failures")]
    private static partial void LogCircuitOpened(ILogger logger, string subscriptionId, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit breaker recovered for subscription {SubscriptionId}")]
    private static partial void LogCircuitRecovered(ILogger logger, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping delivery {DeliveryId} — circuit breaker open for subscription {SubscriptionId}")]
    private static partial void LogCircuitSkipped(ILogger logger, string deliveryId, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error polling pending webhook retries")]
    private static partial void LogPollError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {DeliveryId} delivered to {Url} (HTTP {StatusCode})")]
    private static partial void LogDeliverySuccess(ILogger logger, string deliveryId, string url, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery {DeliveryId} failed unexpectedly")]
    private static partial void LogDeliveryError(ILogger logger, string deliveryId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {DeliveryId} moved to dead letter after {Attempts} attempts")]
    private static partial void LogDeadLetter(ILogger logger, string deliveryId, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {DeliveryId} retry #{Attempts} scheduled for {NextRetry}")]
    private static partial void LogRetryScheduled(ILogger logger, string deliveryId, int attempts, DateTimeOffset nextRetry);
}
