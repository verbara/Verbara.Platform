namespace Asterisk.Platform.Core;

/// <summary>
/// Persistence contract for GDPR purge tombstone records.
/// </summary>
public interface IPurgeLogStore
{
    Task SaveAsync(PurgeEntry entry, CancellationToken ct);
    Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? until,
        int page, int pageSize, CancellationToken ct);
}
