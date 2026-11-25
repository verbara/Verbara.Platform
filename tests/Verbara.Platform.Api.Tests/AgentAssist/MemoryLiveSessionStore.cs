using Verbara.Platform.Api.Services.AgentAssist;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// A shared Monitor list in memory, with the semantics of <see cref="RedisAgentAssistLiveSessionStore"/>: one index per
/// tenant of session ids by expiry, read against <paramref name="time"/>, and a reader that drops an expired member. Every
/// replica of a test holds the same instance, as every replica reads the same Redis.
/// </summary>
internal sealed class MemoryLiveSessionStore(TimeProvider time) : IAgentAssistLiveSessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, (LiveSessionRecord Record, DateTimeOffset ExpiresAt)>> _tenants =
        new(StringComparer.Ordinal);

    /// <summary>How many writes (filings and removals) reached the store.</summary>
    public int Writes { get; private set; }

    public bool IsShared => true;

    public Task FileAsync(LiveSessionRecord record, DateTimeOffset expiresAt, CancellationToken ct)
    {
        lock (_gate)
        {
            Writes++;
            if (!_tenants.TryGetValue(record.OwnerTenant, out var index))
                _tenants[record.OwnerTenant] = index = new(StringComparer.Ordinal);
            index[record.SessionId] = (record, expiresAt);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string ownerTenant, string sessionId, DateTimeOffset? filedUntil, CancellationToken ct)
    {
        lock (_gate)
        {
            Writes++;
            // The Redis store's fence: with filedUntil, only the copy filed with that expiry is removed (to the millisecond).
            if (_tenants.TryGetValue(ownerTenant, out var index)
                && (filedUntil is not { } until
                    || (index.TryGetValue(sessionId, out var entry)
                        && entry.ExpiresAt.ToUnixTimeMilliseconds() == until.ToUnixTimeMilliseconds())))
                index.Remove(sessionId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LiveSessionRecord>> ListAsync(string tenant, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_tenants.TryGetValue(tenant, out var index))
                return Task.FromResult<IReadOnlyList<LiveSessionRecord>>([]);
            foreach (var expired in index.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
                index.Remove(expired);
            return Task.FromResult<IReadOnlyList<LiveSessionRecord>>([.. index.Values.Select(v => v.Record)]);
        }
    }

    public Task<LiveSessionRecord?> FindAsync(string callerTenant, string sessionId, CancellationToken ct)
    {
        // The Redis store's semantics: the member's expiry must be strictly after now.
        var now = time.GetUtcNow();
        lock (_gate)
        {
            return Task.FromResult(
                _tenants.TryGetValue(callerTenant, out var index) && index.TryGetValue(sessionId, out var entry) && entry.ExpiresAt > now
                    ? entry.Record
                    : null);
        }
    }

    /// <summary>The session ids in <paramref name="tenant"/>'s index, expired or not.</summary>
    public IReadOnlyList<string> IndexMembers(string tenant)
    {
        lock (_gate)
            return _tenants.TryGetValue(tenant, out var index) ? [.. index.Keys] : [];
    }
}
