using Verbara.Platform.Core;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Hands over, on every replica, the dispositions stored typifications still owe the dialer
/// (dialer-honours-dnc-callbacks-and-agent-dispositions, design D16): every 5 seconds it claims up to 50 rows that are
/// due and unclaimed, or whose claim expired, and dispatches each through <see cref="DialerDispositionDispatcher"/>.
/// So a request that died mid-hand-off is taken over once its one-minute claim passes, and a failed hand-off is retried
/// within seconds. It resolves its collaborators at each tick, never in its constructor (registered by
/// <c>DialerHostExtensions.AddPlatformDialer</c>).
/// </summary>
public sealed partial class DialerDispositionDispatchWorker : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    internal const int BatchSize = 50;

    private readonly IServiceProvider _services;
    private readonly ILogger<DialerDispositionDispatchWorker> _logger;

    public DialerDispositionDispatchWorker(IServiceProvider services, ILogger<DialerDispositionDispatchWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One pass: claims the due rows and dispatches each, then sets the pending gauge. Cancellation propagates; any
    /// other failure is logged and the next tick tries again. <c>internal</c> for the tests, as
    /// <c>WebhookDeliveryService.ProcessPendingRetriesOnceAsync</c>.
    /// </summary>
    internal async Task ProcessOnceAsync(CancellationToken ct)
    {
        try
        {
            var store = _services.GetRequiredService<IDialerDispositionDispatchStore>();
            var dispatcher = _services.GetRequiredService<DialerDispositionDispatcher>();
            var clock = _services.GetRequiredService<IClock>();

            var now = clock.UtcNow;
            var claimed = await store.ClaimDueAsync(BatchSize, now, now + DialerDispositionDispatcher.Lease, ct)
                .ConfigureAwait(false);
            foreach (var row in claimed)
                await dispatcher.DispatchAsync(row, row.ClaimId!, ct).ConfigureAwait(false);

            _services.GetRequiredService<DialerDispositionMetrics>()
                .SetPending(await store.CountPendingAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPassFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "[DIALER-DISPOSITION] A dispatch pass failed; the next tick tries again.")]
    private partial void LogPassFailed(Exception ex);
}
