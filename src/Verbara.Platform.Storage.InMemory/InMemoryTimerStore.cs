using System.Collections.Concurrent;
using Verbara.Platform.Automation;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryTimerStore : ITimerStore
{
    private readonly ConcurrentDictionary<string, ScheduledTimer> _items = new();

    public Task SaveAsync(ScheduledTimer timer, CancellationToken ct)
    {
        _items[timer.TimerId.Value] = timer;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledTimer>> GetOverdueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        IReadOnlyList<ScheduledTimer> result = _items.Values
            .Where(t => !t.IsFired && t.FireAt <= now)
            .OrderBy(t => t.FireAt)
            .Take(limit)
            .ToList();

        return Task.FromResult(result);
    }

    public Task MarkFiredAsync(ScheduledTimer timer, CancellationToken ct)
    {
        if (_items.TryGetValue(timer.TimerId.Value, out var stored))
            stored.IsFired = true;

        return Task.CompletedTask;
    }
}
