using Asterisk.Platform.Bot;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Services;

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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _collector.Events.Subscribe(async evt =>
        {
            await PersistAsync(evt, stoppingToken).ConfigureAwait(false);
        });

        return Task.CompletedTask;
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
}
