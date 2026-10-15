using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Background service that delivers webhook HTTP POST requests with retry and dead-letter support.
/// Reads new deliveries from WebhookDispatcher's Channel and polls the store for pending retries.
/// </summary>
/// <remarks>
/// Both paths claim a delivery before they POST it, one delivery at a time (webhook-delivery-runs-once, design
/// D2-D8; Platform/ADR-0043): the channel path claims the dequeued delivery by id, the poll claims the next due one.
/// A claim is a committed row change with a lease, so at most one attempt per delivery is in flight across this
/// instance's two loops and every replica. Only the holder of the current claim records the outcome; a stale
/// outcome is discarded. Delivery to the receiver is at-least-once, and <c>X-Webhook-Id</c> is its idempotency key.
/// </remarks>
internal sealed partial class WebhookDeliveryService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the transient-retry <see cref="ResiliencePolicy"/> that wraps each
    /// HTTP send attempt. Separate from the user-visible per-subscription
    /// <see cref="CircuitBreakerPolicy"/>, which persists circuit state on <see cref="WebhookSubscription"/>.
    /// </summary>
    public const string ResiliencePolicyKey = "webhook.delivery";

    /// <summary>
    /// How long a claim keeps every other attempt away from its delivery (design D4). It must outlast the longest
    /// possible attempt, so a live attempt is never taken over; <c>ClaimLease_ShouldExceedWorstCaseAttempt</c> pins
    /// that. A constant on purpose: an option needs a <c>Program.cs</c> binding, and nothing needs tuning.
    /// </summary>
    internal static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>The per-request HTTP timeout of one send.</summary>
    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The period of the retry poll.</summary>
    internal static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The most deliveries one poll tick claims, one after the other (design D3).</summary>
    private const int PollBatchSize = 100;

    private static readonly int[] BackoffSeconds = [0, 60, 300, 1800, 7200, 18000, 28800, 28800];

    private readonly WebhookDispatcher _dispatcher;
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IWebhookSubscriptionStore _subscriptionStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CircuitBreakerPolicy _circuitBreaker;
    private readonly ResiliencePolicy _policy;
    private readonly IClock _clock;
    private readonly ILogger<WebhookDeliveryService> _logger;

    public WebhookDeliveryService(
        WebhookDispatcher dispatcher,
        IWebhookDeliveryStore deliveryStore,
        IWebhookSubscriptionStore subscriptionStore,
        IHttpClientFactory httpClientFactory,
        CircuitBreakerPolicy circuitBreaker,
        IClock clock,
        ILogger<WebhookDeliveryService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _dispatcher = dispatcher;
        _deliveryStore = deliveryStore;
        _subscriptionStore = subscriptionStore;
        _httpClientFactory = httpClientFactory;
        _circuitBreaker = circuitBreaker;
        _clock = clock;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
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
        try
        {
            await foreach (var delivery in _dispatcher.DeliveryReader.ReadAllAsync(ct))
            {
                await DeliverQueuedAsync(delivery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(_logger, nameof(WebhookDeliveryService) + ".ProcessChannelAsync", fatalEx.Message, fatalEx);
            throw;
        }
    }

    private async Task PollPendingRetriesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RetryPollInterval, ct);
                await ProcessPendingRetriesOnceAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(_logger, nameof(WebhookDeliveryService) + ".PollPendingRetriesAsync", fatalEx.Message, fatalEx);
            throw;
        }
    }

    /// <summary>
    /// Runs one poll iteration of the pending-retry sweep: claims the next due delivery and delivers it, then claims
    /// the next one, up to <see cref="PollBatchSize"/> claims (design D3). Reproduces the inner try/catch contract of
    /// <see cref="PollPendingRetriesAsync"/> exactly — cancellation propagates (so the loop can break), but any other
    /// exception is logged and swallowed so the worker keeps spinning (inner-recoverable contract). Exposed
    /// <c>internal</c> for the API test assembly (<c>InternalsVisibleTo</c>) so a single poll
    /// iteration can be exercised deterministically without the 30s <see cref="RetryPollInterval"/>
    /// delay (C3).
    /// </summary>
    internal async Task ProcessPendingRetriesOnceAsync(CancellationToken ct)
    {
        try
        {
            // A tick takes only deliveries due when it starts, as the list it replaced did. A delivery this tick
            // released or rescheduled is due later than that, so the tick never claims it twice, however long it
            // runs. Each claim's lease starts from the clock at that claim.
            var tickStartedAt = _clock.UtcNow;
            for (var claimed = 0; claimed < PollBatchSize && !ct.IsCancellationRequested; claimed++)
            {
                var claim = await _deliveryStore.ClaimNextDueAsync(
                    tickStartedAt, _clock.UtcNow + ClaimLease, ct);
                if (claim is null)
                    break;

                await DeliverClaimedAsync(claim, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation bubbles to the caller's loop, which breaks out of polling.
            throw;
        }
        catch (Exception ex)
        {
            LogPollError(_logger, ex);
        }
    }

    /// <summary>
    /// The channel path for one dequeued delivery: claim it by id, then deliver the claimed row (design D3). A
    /// delivery that cannot be claimed is not sent: another attempt holds it, it is finished, or another claim
    /// statement has it locked at this instant (<c>SKIP LOCKED</c>, design D2). In the last case it is still due,
    /// so its lock holder or the next poll sends it, at most <see cref="RetryPollInterval"/> later.
    /// </summary>
    private async Task DeliverQueuedAsync(WebhookDelivery queued, CancellationToken ct)
    {
        WebhookDeliveryClaim? claim;
        try
        {
            var now = _clock.UtcNow;
            claim = await _deliveryStore.TryClaimAsync(queued.DeliveryId, now, now + ClaimLease, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The row is saved and still due: the poll sends it. A failed claim must not stop the channel loop.
            LogQueuedClaimError(_logger, queued.DeliveryId, ex);
            return;
        }

        if (claim is null)
        {
            LogQueuedNotClaimable(_logger, queued.DeliveryId);
            return;
        }

        await DeliverClaimedAsync(claim, ct);
    }

    /// <summary>
    /// One attempt under <paramref name="claim"/>. It sends the claimed row, never a caller's copy: that row's
    /// payload is the body every request of the delivery carries (spec: same <c>X-Webhook-Id</c> and body).
    /// </summary>
    private async Task DeliverClaimedAsync(WebhookDeliveryClaim claim, CancellationToken ct)
    {
        var delivery = claim.Delivery;
        if (claim.Reclaimed)
            LogReclaimed(_logger, delivery.DeliveryId, claim.ClaimId);

        try
        {
            var sub = await _subscriptionStore.GetByIdAsync(delivery.SubscriptionId, ct);
            if (sub is null)
            {
                // Subscription deleted — mark as dead letter. No POST was sent, so attempts is unchanged (design D5).
                var orphaned = delivery with
                {
                    Status = WebhookDeliveryStatus.DeadLetter,
                    LastError = "Subscription deleted",
                };
                await CompleteAttemptAsync(orphaned, claim, ct);
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

            // Circuit open: release the claim without an attempt, and defer the delivery to the circuit's next probe
            // (or the next poll when it has none). Leaving it due would let this tick claim it again (design D6).
            if (!CircuitBreakerPolicy.ShouldDeliver(sub, now))
            {
                var nextDueAt = sub.CircuitNextProbeAt is { } probeAt && probeAt > now
                    ? probeAt
                    : now + RetryPollInterval;
                if (await _deliveryStore.ReleaseClaimAsync(delivery.DeliveryId, claim.ClaimId, nextDueAt, ct))
                    LogCircuitSkipped(_logger, delivery.DeliveryId, delivery.SubscriptionId, nextDueAt);
                else
                    LogStaleOutcomeDiscarded(_logger, "circuit-open release", delivery.DeliveryId, claim.ClaimId);
                return;
            }

            var client = _httpClientFactory.CreateClient("webhooks");
            client.Timeout = HttpTimeout;

            var timestamp = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var signature = WebhookSignatureService.ComputeSignature(timestamp, delivery.Payload, sub.Secret);

            // Transient-retry layer: wrap only the HTTP send, leaving the user-visible
            // 8-attempt BackoffSeconds schedule (scheduled via store) and the per-subscription
            // CircuitBreakerPolicy untouched. The inner policy protects a single attempt
            // against transient upstream blips (retry 3/500ms, circuit 5/30s, timeout 10s).
            // HttpRequestMessage is built per-attempt because Content is consumed on send.
            using var response = await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, sub.EndpointUrl);
                    request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
                    request.Headers.Add("X-Webhook-Id", delivery.DeliveryId);
                    request.Headers.Add("X-Webhook-Event", delivery.EventType);
                    request.Headers.Add("X-Webhook-Timestamp", timestamp);
                    request.Headers.Add("X-Webhook-Signature", signature);
                    return await client.SendAsync(request, innerCt).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
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
                if (await CompleteAttemptAsync(delivered, claim, ct))
                    LogDeliverySuccess(_logger, delivery.DeliveryId, sub.EndpointUrl, statusCode);
            }
            else
            {
                await HandleFailureAsync(claim, sub, statusCode, $"HTTP {statusCode}", ct);
            }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the claim stays, and the delivery is reclaimed once its lease has passed (design D7).
        }
        catch (TaskCanceledException)
        {
            // HTTP timeout — need subscription for circuit breaker; re-fetch if possible
            var sub = await TryGetSubscriptionAsync(delivery.SubscriptionId, ct);
            await HandleFailureAsync(claim, sub, null, "Request timeout", ct);
        }
        catch (HttpRequestException ex)
        {
            var sub = await TryGetSubscriptionAsync(delivery.SubscriptionId, ct);
            await HandleFailureAsync(claim, sub, null, $"Network error: {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            LogDeliveryError(_logger, delivery.DeliveryId, ex);
            var sub = await TryGetSubscriptionAsync(delivery.SubscriptionId, ct);
            await HandleFailureAsync(claim, sub, null, $"Unexpected error: {ex.Message}", ct);
        }
    }

    private async Task<WebhookSubscription?> TryGetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        try { return await _subscriptionStore.GetByIdAsync(subscriptionId, ct); }
        catch { return null; }
    }

    private async Task HandleFailureAsync(
        WebhookDeliveryClaim claim, WebhookSubscription? sub, int? responseCode, string error, CancellationToken ct)
    {
        var delivery = claim.Delivery;

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
            if (await CompleteAttemptAsync(deadLetter, claim, ct))
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
            if (await CompleteAttemptAsync(retry, claim, ct))
                LogRetryScheduled(_logger, delivery.DeliveryId, newAttempts, nextRetry);
        }
    }

    /// <summary>
    /// Records <paramref name="outcome"/> under <paramref name="claim"/>, fenced by its claim id (design D5). When
    /// another attempt has taken the claim over, nothing is written: the stale outcome is logged and discarded.
    /// </summary>
    /// <returns>Whether the outcome was recorded.</returns>
    private async Task<bool> CompleteAttemptAsync(
        WebhookDelivery outcome, WebhookDeliveryClaim claim, CancellationToken ct)
    {
        if (await _deliveryStore.CompleteAttemptAsync(outcome, claim.ClaimId, ct))
            return true;

        LogStaleOutcomeDiscarded(_logger, outcome.Status.ToString(), outcome.DeliveryId, claim.ClaimId);
        return false;
    }

    /// <summary>
    /// Backoff schedule for external consumers (e.g., tests).
    /// </summary>
    internal static int GetBackoffSeconds(int attemptNumber)
    {
        var index = Math.Min(attemptNumber, BackoffSeconds.Length - 1);
        return BackoffSeconds[index];
    }

    /// <summary>
    /// Test-only entry point into the channel path for one dequeued delivery (claim by id, then deliver). Exposed
    /// via <c>InternalsVisibleTo</c> to the API test project so it can be exercised without starting the
    /// BackgroundService loop.
    /// </summary>
    internal Task DeliverForTestAsync(WebhookDelivery delivery, CancellationToken ct)
        => DeliverQueuedAsync(delivery, ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit breaker opened for subscription {SubscriptionId} after {Failures} consecutive failures")]
    private static partial void LogCircuitOpened(ILogger logger, string subscriptionId, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit breaker recovered for subscription {SubscriptionId}")]
    private static partial void LogCircuitRecovered(ILogger logger, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping delivery {DeliveryId} — circuit breaker open for subscription {SubscriptionId}; released without an attempt, next due at {NextDueAt}")]
    private static partial void LogCircuitSkipped(ILogger logger, string deliveryId, string subscriptionId, DateTimeOffset nextDueAt);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {DeliveryId} reclaimed under claim {ClaimId}: an earlier attempt's claim expired without an outcome, so it is sent again (receivers deduplicate on X-Webhook-Id)")]
    private static partial void LogReclaimed(ILogger logger, string deliveryId, string claimId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarded the {Outcome} outcome of webhook {DeliveryId}: its claim {ClaimId} is no longer current (another attempt took the delivery over after the lease expired, or the delivery was deleted)")]
    private static partial void LogStaleOutcomeDiscarded(ILogger logger, string outcome, string deliveryId, string claimId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Webhook {DeliveryId} not sent on the immediate path: it is claimed by another attempt, already finished, or being claimed by the retry poll")]
    private static partial void LogQueuedNotClaimable(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not claim webhook {DeliveryId} on the immediate path; it stays due, and the retry poll sends it")]
    private static partial void LogQueuedClaimError(ILogger logger, string deliveryId, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private static partial void LogWorkerCrash(ILogger logger, string workerName, string reason, Exception ex);
}
