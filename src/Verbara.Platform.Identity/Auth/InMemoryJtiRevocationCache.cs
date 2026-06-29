using System.Collections.Concurrent;

namespace Verbara.Platform.Identity.Auth;

/// <summary>
/// In-memory <see cref="IJtiRevocationCache"/> default — single-process safe. For
/// multi-instance deploys use the Redis-backed implementation in
/// <c>Verbara.Platform.Identity.Redis</c>.
/// </summary>
public sealed class InMemoryJtiRevocationCache : IJtiRevocationCache
{
    // Maps jti → absolute expiry time of the original token.
    // Entries whose expiry is in the past are pruned on each lookup (lock-free).
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Production ctor — uses the real system clock. Keeps the DI registration parameterless.</summary>
    public InMemoryJtiRevocationCache() : this(TimeProvider.System)
    {
    }

    /// <summary>Test seam — injects a <see cref="TimeProvider"/> so TTL pruning can be driven deterministically.</summary>
    public InMemoryJtiRevocationCache(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <inheritdoc />
    public ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct)
    {
        if (!_revoked.TryGetValue(jti, out var expiresAt))
            return ValueTask.FromResult(false);

        // Prune expired entry opportunistically — token is already expired, no longer a threat
        if (expiresAt <= _clock.GetUtcNow())
        {
            _revoked.TryRemove(jti, out _);
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // Only store entries that haven't already expired
        if (expiresAt > _clock.GetUtcNow())
            _revoked[jti] = expiresAt;

        return ValueTask.CompletedTask;
    }
}
