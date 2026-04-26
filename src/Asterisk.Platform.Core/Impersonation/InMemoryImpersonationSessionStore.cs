using System.Collections.Concurrent;

namespace Asterisk.Platform.Core.Impersonation;

/// <summary>
/// Default thread-safe in-memory implementation of
/// <see cref="IImpersonationSessionStore"/>. Suitable for single-process
/// deployments and the integration test factories. Multi-instance Platform
/// deployments will swap this for a Redis or Postgres-backed store via
/// <c>services.AddSingleton&lt;IImpersonationSessionStore, ...&gt;()</c>
/// override.
/// </summary>
public sealed class InMemoryImpersonationSessionStore : IImpersonationSessionStore
{
    private readonly ConcurrentDictionary<string, ImpersonationSession> _sessions = new(StringComparer.Ordinal);

    public Task AddAsync(ImpersonationSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<ImpersonationSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult<ImpersonationSession?>(session);
    }

    public Task<IReadOnlyList<ImpersonationSession>> GetActiveAsync(CancellationToken ct)
    {
        IReadOnlyList<ImpersonationSession> snapshot = _sessions.Values
            .Where(s => s.Status == ImpersonationSessionStatus.Active)
            .OrderBy(s => s.StartedAt)
            .ToList();
        return Task.FromResult(snapshot);
    }

    public Task<PagedResult<ImpersonationSession>> ListActiveAsync(
        string? actorTenantId, int page, int pageSize, CancellationToken ct)
    {
        var (p, ps) = ClampPaging(page, pageSize);
        var query = _sessions.Values
            .Where(s => s.Status == ImpersonationSessionStatus.Active);
        if (!string.IsNullOrEmpty(actorTenantId))
        {
            query = query.Where(s => string.Equals(
                s.ActorTenantId, actorTenantId, StringComparison.Ordinal));
        }

        var ordered = query.OrderByDescending(s => s.StartedAt).ToList();
        var page1 = ordered.Skip((p - 1) * ps).Take(ps).ToList();
        return Task.FromResult(new PagedResult<ImpersonationSession>(page1, ordered.Count, p, ps));
    }

    public Task<PagedResult<ImpersonationSession>> ListHistoryAsync(
        string? actorTenantId,
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toInclusive,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var (p, ps) = ClampPaging(page, pageSize);
        var query = _sessions.Values
            .Where(s => s.Status != ImpersonationSessionStatus.Active);
        if (!string.IsNullOrEmpty(actorTenantId))
        {
            query = query.Where(s => string.Equals(
                s.ActorTenantId, actorTenantId, StringComparison.Ordinal));
        }
        if (fromInclusive is { } f)
            query = query.Where(s => s.StartedAt >= f);
        if (toInclusive is { } t)
            query = query.Where(s => s.StartedAt <= t);

        var ordered = query.OrderByDescending(s => s.StartedAt).ToList();
        var page1 = ordered.Skip((p - 1) * ps).Take(ps).ToList();
        return Task.FromResult(new PagedResult<ImpersonationSession>(page1, ordered.Count, p, ps));
    }

    public Task<int> CountActiveByActorTenantAsync(string actorTenantId, CancellationToken ct)
    {
        var count = _sessions.Values.Count(s =>
            s.Status == ImpersonationSessionStatus.Active &&
            string.Equals(s.ActorTenantId, actorTenantId, StringComparison.Ordinal));
        return Task.FromResult(count);
    }

    public Task<bool> RevokeAsync(
        string sessionId,
        ImpersonationSessionStatus status,
        string? reason,
        DateTimeOffset endedAt,
        CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult(false);

        // Idempotent — already-closed sessions are a no-op.
        if (session.Status != ImpersonationSessionStatus.Active)
            return Task.FromResult(false);

        session.Status = status;
        session.CloseReason = reason;
        session.EndedAt = endedAt;
        return Task.FromResult(true);
    }

    private static (int Page, int PageSize) ClampPaging(int page, int pageSize)
    {
        var p = page > 0 ? page : 1;
        var ps = pageSize switch
        {
            <= 0 => 50,
            > 500 => 500,
            _ => pageSize,
        };
        return (p, ps);
    }
}
