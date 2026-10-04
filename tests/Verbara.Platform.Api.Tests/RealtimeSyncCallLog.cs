using NSubstitute;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Ordered log of the queue-member writes a substituted <see cref="IRealtimeSyncService"/> received:
/// <c>AddQueueMemberAsync</c> and <c>SyncAgentPausedAsync:{paused}</c>. Lets a test assert that the
/// paused convergence write came after the upsert without <c>Received.InOrder</c>, which CA2012 rejects
/// for ValueTask-returning members.
/// </summary>
internal static class RealtimeSyncCallLog
{
    public static IReadOnlyList<string> Of(IRealtimeSyncService sync) =>
        sync.ReceivedCalls()
            .Select(c => (Name: c.GetMethodInfo().Name, Args: c.GetArguments()))
            .Where(c => c.Name is nameof(IRealtimeSyncService.AddQueueMemberAsync)
                or nameof(IRealtimeSyncService.SyncAgentPausedAsync))
            .Select(c => c.Name == nameof(IRealtimeSyncService.SyncAgentPausedAsync)
                ? $"{c.Name}:{c.Args[2]}"
                : c.Name)
            .ToList();
}
