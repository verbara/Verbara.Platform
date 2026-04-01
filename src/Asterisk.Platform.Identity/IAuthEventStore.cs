using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IAuthEventStore
{
    Task SaveAsync(AuthEvent authEvent, CancellationToken ct);
    Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<AuthEvent>> SearchAsync(string tenantId, AuthEventQuery query, CancellationToken ct);

    /// <summary>Returns all auth events for a user without pagination (GDPR export).</summary>
    Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct);

    /// <summary>Deletes all auth events for a user and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct);

    /// <summary>Deletes auth events older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
