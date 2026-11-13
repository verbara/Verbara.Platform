using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.Workers.Resilience;

/// <summary>
/// Helpers shared by the per-worker resilience tests. The contract under test is the
/// outer <c>try-catch</c> on every hardened <see cref="BackgroundService"/>:
/// <list type="bullet">
///   <item>fatal (non-cancellation) exceptions surface on
///         <see cref="BackgroundService.ExecuteTask"/> so the host stops;</item>
///   <item><see cref="OperationCanceledException"/> from <c>stoppingToken</c>
///         is swallowed (clean shutdown).</item>
/// </list>
/// </summary>
internal static class WorkerResilienceTestHelpers
{
    /// <summary>
    /// Waits for the worker's <see cref="BackgroundService.ExecuteTask"/> to complete
    /// or fault, with a hard wall-clock cap so a misbehaving worker can't hang the suite.
    /// </summary>
    public static async Task<Exception?> AwaitExecuteFaultAsync(
        BackgroundService worker,
        TimeSpan timeout)
    {
        var executeTask = worker.ExecuteTask;
        if (executeTask is null)
            return null;

        // GUARD, not a sync fence: this is the fault-or-timeout race — the timeout is a hard cap so a hung worker can't stall the suite. Do NOT migrate it to a causal signal.
        // WaitAsync cancels its timer when the worker finishes first (CA2027); SuppressThrowing keeps a fault, a cancellation
        // and the timeout from throwing here — the fault is read from the task below, the timeout returns null.
        await executeTask.WaitAsync(timeout)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (!executeTask.IsCompleted)
            return null;

        return executeTask.Exception?.GetBaseException();
    }
}
