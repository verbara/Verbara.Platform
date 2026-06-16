using System.Collections.Concurrent;
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Default <see cref="ITypificationTokenBudget"/>: a thread-safe, in-process accumulator that
/// tracks the running LLM-token sum per <c>(tenant, UTC-day)</c>. The "today" boundary is
/// derived from the injected <see cref="IClock"/> so it rolls over deterministically at the
/// UTC midnight (and is testable with a fake clock).
/// </summary>
/// <remarks>
/// <para>
/// <b>Accuracy scope (per the plan — "persisted or cached"):</b> this is a single-instance,
/// in-memory accumulator. It is exactly accurate for a single API instance; a horizontally
/// scaled (multi-instance) deployment would need a shared store (Redis) so the per-tenant sum
/// is global rather than per-pod. That is a future enhancement — the interface is the seam.
/// </para>
/// <para>
/// <b>Old days:</b> entries for past UTC days are harmless — they are simply never read again
/// (lookups key on the current day). A lazy prune drops stale keys opportunistically on write
/// to keep the dictionary bounded; correctness does not depend on it.
/// </para>
/// <para>Registered as a <b>singleton</b> in <c>AddPlatformTypification()</c>.</para>
/// </remarks>
public sealed class InMemoryTypificationTokenBudget : ITypificationTokenBudget
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<DayKey, long> _sums = new();

    public InMemoryTypificationTokenBudget(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public Task<bool> IsOverBudgetAsync(TenantId tenantId, long dailyBudget, CancellationToken ct)
    {
        var current = _sums.TryGetValue(KeyForToday(tenantId), out var sum) ? sum : 0L;
        // Boundary inclusive: reaching the budget exhausts it (>=), matching the plan's "≥".
        return Task.FromResult(current >= dailyBudget);
    }

    public Task RecordUsageAsync(TenantId tenantId, long tokens, CancellationToken ct)
    {
        if (tokens <= 0)
            return Task.CompletedTask;

        var today = KeyForToday(tenantId);
        _sums.AddOrUpdate(today, tokens, (_, existing) => existing + tokens);

        // Lazy prune: drop any accumulated entries from earlier UTC days so the dictionary
        // does not grow without bound over a long-running process. Cheap and correctness-neutral.
        if (_sums.Count > 1)
        {
            foreach (var key in _sums.Keys)
            {
                if (key.DayUtc < today.DayUtc)
                    _sums.TryRemove(key, out _);
            }
        }

        return Task.CompletedTask;
    }

    private DayKey KeyForToday(TenantId tenantId) =>
        new(tenantId.Value, DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime));

    /// <summary>Composite key: a tenant's accumulator for one UTC calendar day.</summary>
    private readonly record struct DayKey(string TenantId, DateOnly DayUtc);
}
