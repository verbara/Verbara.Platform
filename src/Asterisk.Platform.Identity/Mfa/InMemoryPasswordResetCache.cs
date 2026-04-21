using System.Collections.Concurrent;

namespace Asterisk.Platform.Identity.Mfa;

/// <summary>
/// In-memory implementation of <see cref="IPasswordResetCache"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///
/// Stale entries (reset tokens that were never consumed) remain in the dictionary
/// until a <c>TakeAsync</c> call finds them expired. With a 1-hour reset TTL and
/// typical low volume this is negligible. A Redis-backed implementation is
/// deferred to v1.9.3+.
/// </summary>
public sealed class InMemoryPasswordResetCache : IPasswordResetCache
{
    private readonly ConcurrentDictionary<string, PasswordResetEntry> _cache = new();

    public ValueTask StoreAsync(string resetToken, PasswordResetEntry entry, CancellationToken ct)
    {
        _cache[resetToken] = entry;
        return ValueTask.CompletedTask;
    }

    public ValueTask<PasswordResetEntry?> TakeAsync(string resetToken, CancellationToken ct)
    {
        if (!_cache.TryRemove(resetToken, out var entry))
            return ValueTask.FromResult<PasswordResetEntry?>(null);

        return ValueTask.FromResult(entry.ExpiresAt > DateTimeOffset.UtcNow ? entry : null);
    }
}
