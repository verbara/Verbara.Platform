using System.Collections.Concurrent;

namespace Verbara.Platform.Identity.Mfa;

/// <summary>
/// In-memory implementation of <see cref="IMfaPendingCache"/> for a single process.
/// </summary>
/// <remarks>
/// Each challenge is a slot in a <see cref="ConcurrentDictionary{TKey,TValue}"/>; reserving an attempt and
/// consuming the challenge happen under that slot's lock, so the attempt count and the removal are one
/// step. A slot is removed only as itself (<c>TryRemove(KeyValuePair)</c>), never a challenge re-stored
/// under the same token in the meantime.
///
/// Stale entries (challenges that were never attempted) remain in the dictionary until an attempt finds
/// them expired. With a 5-minute challenge TTL and typically ≤1 000 concurrent pending challenges this is
/// negligible memory; a background eviction sweep is not implemented. Multi-replica deployments use the
/// Redis implementation.
/// </remarks>
public sealed class InMemoryMfaPendingCache : IMfaPendingCache
{
    private readonly ConcurrentDictionary<string, Slot> _cache = new(StringComparer.Ordinal);

    public ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeToken);
        ArgumentNullException.ThrowIfNull(entry);
        _cache[challengeToken] = new Slot(entry);
        return ValueTask.CompletedTask;
    }

    public ValueTask<MfaChallengeAttempt?> TryBeginAttemptAsync(string? challengeToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(challengeToken) || !_cache.TryGetValue(challengeToken, out var slot))
            return ValueTask.FromResult<MfaChallengeAttempt?>(null);

        MfaChallengeAttempt? attempt = null;
        lock (slot.Gate)
        {
            if (!slot.Removed && slot.Entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                slot.Used++;
                var isFinal = slot.Used >= MfaChallenge.MaxAttempts;
                if (isFinal)
                    slot.Removed = true;
                attempt = new MfaChallengeAttempt(slot.Entry, slot.Used, isFinal);
            }
            else
            {
                slot.Removed = true;
            }
        }

        if (slot.Removed)
            _cache.TryRemove(new KeyValuePair<string, Slot>(challengeToken, slot));

        return ValueTask.FromResult(attempt);
    }

    public ValueTask<bool> TryConsumeAsync(string? challengeToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(challengeToken) || !_cache.TryGetValue(challengeToken, out var slot))
            return ValueTask.FromResult(false);

        bool consumed;
        lock (slot.Gate)
        {
            consumed = !slot.Removed && slot.Entry.ExpiresAt > DateTimeOffset.UtcNow;
            slot.Removed = true;
        }

        _cache.TryRemove(new KeyValuePair<string, Slot>(challengeToken, slot));
        return ValueTask.FromResult(consumed);
    }

    private sealed class Slot(MfaPendingEntry entry)
    {
        public Lock Gate { get; } = new();

        public MfaPendingEntry Entry { get; } = entry;

        public int Used { get; set; }

        public bool Removed { get; set; }
    }
}
