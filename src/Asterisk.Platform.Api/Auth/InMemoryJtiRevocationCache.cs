using System.Collections.Concurrent;

namespace Asterisk.Platform.Api.Auth;

internal sealed class InMemoryJtiRevocationCache : IJtiRevocationCache
{
    // Maps jti → absolute expiry time of the original token.
    // Entries whose expiry is in the past are pruned on each lookup (lock-free).
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new(StringComparer.Ordinal);

    public ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct)
    {
        if (!_revoked.TryGetValue(jti, out var expiresAt))
            return ValueTask.FromResult(false);

        // Prune expired entry opportunistically — token is already expired, no longer a threat
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            _revoked.TryRemove(jti, out _);
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // Only store entries that haven't already expired
        if (expiresAt > DateTimeOffset.UtcNow)
            _revoked[jti] = expiresAt;

        return ValueTask.CompletedTask;
    }
}
