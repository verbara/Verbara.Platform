using System.Collections.Concurrent;

namespace Asterisk.Platform.Identity.Mfa;

/// <summary>
/// In-memory implementation of <see cref="IMfaPendingCache"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///
/// Stale entries (challenge tokens that were never verified) remain in the
/// dictionary until a <c>TakeAsync</c> call finds them expired. With a 5-minute
/// challenge TTL and typical ≤1 000 concurrent pending challenges this is
/// negligible memory. A background eviction sweep is deferred to v1.9.3+
/// when a Redis-backed implementation is introduced.
/// </summary>
public sealed class InMemoryMfaPendingCache : IMfaPendingCache
{
    private readonly ConcurrentDictionary<string, MfaPendingEntry> _cache = new();

    public ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct)
    {
        _cache[challengeToken] = entry;
        return ValueTask.CompletedTask;
    }

    public ValueTask<MfaPendingEntry?> TakeAsync(string challengeToken, CancellationToken ct)
    {
        if (!_cache.TryRemove(challengeToken, out var entry))
            return ValueTask.FromResult<MfaPendingEntry?>(null);

        return ValueTask.FromResult(entry.ExpiresAt > DateTimeOffset.UtcNow ? entry : null);
    }
}
