using System.Collections.Concurrent;

namespace Asterisk.Platform.Identity.Auth.Jwt;

/// <summary>
/// In-memory <see cref="IJwtKeyStore"/> default — single-process safe. For
/// multi-instance deploys use the Redis-backed implementation in
/// <c>Asterisk.Platform.Identity.Redis</c>.
/// </summary>
/// <remarks>
/// R5.4 S5.9 — When marking a key active, demotes any currently-active entry
/// to keep the invariant that at most one entry has
/// <see cref="JwtKeyEntry.IsActive"/> == <see langword="true"/>.
/// </remarks>
public sealed class InMemoryJwtKeyStore : IJwtKeyStore
{
    private readonly ConcurrentDictionary<string, JwtKeyEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyList<JwtKeyEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JwtKeyEntry>>(_entries.Values.ToArray());

    /// <inheritdoc />
    public Task<JwtKeyEntry?> GetActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(_entries.Values.FirstOrDefault(e => e.IsActive));

    /// <inheritdoc />
    public Task UpsertAsync(JwtKeyEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // If marking active, demote any currently-active entry to non-active
        // so the "at most one active key" invariant holds.
        if (entry.IsActive)
        {
            foreach (var kv in _entries)
            {
                if (kv.Value.IsActive && !string.Equals(kv.Key, entry.KeyId, StringComparison.Ordinal))
                    _entries[kv.Key] = kv.Value with { IsActive = false };
            }
        }
        _entries[entry.KeyId] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt < asOf)
                _entries.TryRemove(kv.Key, out _);
        }
        return Task.CompletedTask;
    }
}
