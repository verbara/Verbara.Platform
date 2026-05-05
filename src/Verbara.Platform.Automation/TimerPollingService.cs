using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Automation;

public sealed partial class TimerPollingService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-tick <see cref="ResiliencePolicy"/> that wraps each
    /// timer-polling pass. Circuit-open skips the current tick; the next 15s tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.timer-polling";

    private const int TimerPollIntervalSeconds = 15;
    private const int TimerBatchLimit = 50;

    private readonly ITimerStore _timerStore;
    private readonly IAutomationEngine _automationEngine;
    private readonly IClock _clock;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<TimerPollingService> _logger;

    public TimerPollingService(
        ITimerStore timerStore,
        IAutomationEngine automationEngine,
        IClock clock,
        ILogger<TimerPollingService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _timerStore = timerStore;
        _automationEngine = automationEngine;
        _clock = clock;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(_logger);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TimerPollIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _policy.ExecuteAsync(
                    ResiliencePolicyKey,
                    async innerCt =>
                    {
                        await PollAsync(innerCt).ConfigureAwait(false);
                        return 0;
                    },
                    stoppingToken).ConfigureAwait(false);
            }
            catch (CircuitBreakerOpenException)
            {
                LogCircuitOpen(_logger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPollCycleError(_logger, ex);
            }
        }
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var overdueTimers = await _timerStore.GetOverdueAsync(now, TimerBatchLimit, ct).ConfigureAwait(false);

        if (overdueTimers.Count == 0)
            return;

        LogProcessingTimers(_logger, overdueTimers.Count);

        foreach (var scheduledTimer in overdueTimers)
        {
            var automationEvent = new AutomationEvent
            {
                Trigger = AutomationTrigger.TimerElapsed,
                TenantId = scheduledTimer.TenantId,
                ConversationId = scheduledTimer.ConversationId,
                OccurredAt = now,
            };

            try
            {
                await _automationEngine.ProcessEventAsync(automationEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogTimerProcessingError(_logger, ex, scheduledTimer.TimerId.Value);
            }
            finally
            {
                await _timerStore.MarkFiredAsync(scheduledTimer, ct).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "TimerPollingService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Count} overdue timer(s)")]
    private static partial void LogProcessingTimers(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing timer {TimerId}")]
    private static partial void LogTimerProcessingError(ILogger logger, Exception ex, string timerId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.timer-polling — skipping tick")]
    private static partial void LogCircuitOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timer polling tick failed")]
    private static partial void LogPollCycleError(ILogger logger, Exception ex);
}
