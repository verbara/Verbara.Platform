namespace Verbara.Platform.Core.Impersonation;

/// <summary>
/// R5.2 PB.2 — read/write surface for active + historical impersonation
/// sessions. Implementations must be thread-safe (the store is hit from the
/// <c>POST /management/impersonate</c> hot path and the periodic timeout
/// sweep concurrently).
/// </summary>
public interface IImpersonationSessionStore
{
    /// <summary>Persist a newly started session.</summary>
    Task AddAsync(ImpersonationSession session, CancellationToken ct);

    /// <summary>Get a single session by id (active or historical).</summary>
    Task<ImpersonationSession?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Returns every session whose <see cref="ImpersonationSession.Status"/> is
    /// <see cref="ImpersonationSessionStatus.Active"/>. Used by the admin list
    /// view + the timeout sweep.
    /// </summary>
    Task<IReadOnlyList<ImpersonationSession>> GetActiveAsync(CancellationToken ct);

    /// <summary>
    /// Paginated active-session view. <paramref name="actorTenantId"/>
    /// optionally narrows to a single actor tenant (PlatformAdmin sees all
    /// tenants when null).
    /// </summary>
    Task<PagedResult<ImpersonationSession>> ListActiveAsync(
        string? actorTenantId, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Paginated historical view filtered by start range. <paramref name="actorTenantId"/>
    /// optionally narrows to a single tenant.
    /// </summary>
    Task<PagedResult<ImpersonationSession>> ListHistoryAsync(
        string? actorTenantId,
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toInclusive,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>
    /// Counts active sessions for the given actor tenant. Used by the
    /// max-concurrent-sessions enforcement at session start.
    /// </summary>
    Task<int> CountActiveByActorTenantAsync(string actorTenantId, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="sessionId"/> as ended with the supplied reason +
    /// status. No-op if the session is already non-active (idempotent — the
    /// timeout sweep can race with manual revoke). Returns true if the session
    /// transitioned from active to closed; false otherwise.
    /// </summary>
    Task<bool> RevokeAsync(
        string sessionId,
        ImpersonationSessionStatus status,
        string? reason,
        DateTimeOffset endedAt,
        CancellationToken ct);
}
