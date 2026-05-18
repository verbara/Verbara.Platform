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

        var winner = await Task.WhenAny(executeTask, Task.Delay(timeout));
        if (winner != executeTask)
            return null;

        return executeTask.Exception?.GetBaseException();
    }
}
