using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit;

/// <summary>
/// Default implementation of <see cref="IAuditService"/>.
/// Creates an <see cref="AuditEntry"/> and delegates persistence to <see cref="IAuditStore"/>.
/// </summary>
public sealed class DefaultAuditService : IAuditService
{
    private readonly IAuditStore _store;
    private readonly IClock _clock;

    /// <summary>Initialises a new instance with the given store and clock.</summary>
    public DefaultAuditService(IAuditStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    /// <inheritdoc />
    public Task LogAsync(
        TenantId tenantId,
        string action,
        string entityType,
        string entityId,
        string? performedBy = null,
        IReadOnlyDictionary<string, string>? details = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            PerformedBy = performedBy,
            Details = details,
            OccurredAt = _clock.UtcNow,
        };

        return _store.SaveAsync(entry, ct);
    }
}
