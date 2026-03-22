using Asterisk.Platform.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Automation;

public sealed partial class TimerPollingService : BackgroundService
{
    private const int TimerPollIntervalSeconds = 15;
    private const int TimerBatchLimit = 50;

    private readonly ITimerStore _timerStore;
    private readonly IAutomationEngine _automationEngine;
    private readonly IClock _clock;
    private readonly ILogger<TimerPollingService> _logger;

    public TimerPollingService(
        ITimerStore timerStore,
        IAutomationEngine automationEngine,
        IClock clock,
        ILogger<TimerPollingService> logger)
    {
        _timerStore = timerStore;
        _automationEngine = automationEngine;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(_logger);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TimerPollIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
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
}
