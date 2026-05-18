using Verbara.Platform.Bot;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

internal sealed partial class BotAnalyticsPersistenceService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-event <see cref="ResiliencePolicy"/> that wraps each
    /// DB persistence call. Circuit-open drops the current event with a debug log; subsequent
    /// events continue to be attempted (no outer loop to short-circuit here — this is an
    /// IObservable subscription).
    /// </summary>
    public const string ResiliencePolicyKey = "worker.bot-analytics-persistence";

    private readonly BotAnalyticsCollector _collector;
    private readonly IBotAnalyticsStore _store;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<BotAnalyticsPersistenceService> _logger;
    private IDisposable? _subscription;

    public BotAnalyticsPersistenceService(
        BotAnalyticsCollector collector,
        IBotAnalyticsStore store,
        ILogger<BotAnalyticsPersistenceService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _collector = collector;
        _store = store;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    /// <summary>
    /// Exposes whether the upstream observable subscription is still attached.
    /// A health check can short-circuit to Unhealthy when this returns <c>false</c>
    /// (i.e. <see cref="HandleSubscriptionFault"/> has nullified it after an
    /// <c>OnError</c> from the source stream).
    /// </summary>
    internal bool IsSubscriptionHealthy => Volatile.Read(ref _subscription) is not null;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _subscription = _collector.Events.Subscribe(
                onNext: evt => HandleEventSafely(evt, stoppingToken),
                onError: HandleSubscriptionFault,
                onCompleted: () => { /* normal completion — host shutting down */ });

            stoppingToken.Register(() => _subscription?.Dispose());
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            // Fatal crash during subscription wiring — Critical log + rethrow so
            // the host can stop and be restarted by the orchestrator.
            LogWorkerCrash(nameof(BotAnalyticsPersistenceService), ex.Message, ex);
            throw;
        }
    }

    private void HandleEventSafely(BotAnalyticsEvent evt, CancellationToken ct)
    {
        try
        {
            // Fire-and-forget is intentional — PersistAsync has its own try-catch
            // (CircuitBreakerOpenException / OperationCanceledException / Exception).
            // This wrapper only catches synchronous throws prior to the await.
            _ = PersistAsync(evt, ct);
        }
        catch (Exception ex)
        {
            LogFireAndForgetSwallowed(ex.Message);
        }
    }

    private void HandleSubscriptionFault(Exception ex)
    {
        LogSubscriptionFault(ex.Message);
        // Nullify so health checks can report Unhealthy via IsSubscriptionHealthy.
        Interlocked.Exchange(ref _subscription, null);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    internal async Task PersistAsync(BotAnalyticsEvent evt, CancellationToken ct)
    {
        try
        {
            var record = new BotAnalyticsRecord
            {
                EventType = evt.Type.ToString(),
                BotId = evt.BotId.Value,
                ConversationId = evt.ConversationId.Value,
                TurnCount = evt.TurnCount ?? 0,
                HandoffReason = evt.HandoffReason,
                CreatedAt = evt.OccurredAt,
            };

            await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    await _store.RecordEventAsync(evt.TenantId, record, innerCt);
                    return 0;
                },
                ct);
        }
        catch (CircuitBreakerOpenException)
        {
            // Circuit open for DB — drop this event; observables are best-effort.
            LogCircuitOpen();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — drop silently.
        }
        catch (Exception ex)
        {
            LogPersistError(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist bot analytics event")]
    private partial void LogPersistError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.bot-analytics-persistence — dropping event")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[BOT-ANALYTICS] Subscription faulted — health check will report Unhealthy. Reason: {Reason}")]
    private partial void LogSubscriptionFault(string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[BOT-ANALYTICS] Fire-and-forget execution failed: {Reason}")]
    private partial void LogFireAndForgetSwallowed(string reason);
}
