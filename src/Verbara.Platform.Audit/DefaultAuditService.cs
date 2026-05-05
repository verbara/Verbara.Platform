using Verbara.Platform.Core;

namespace Verbara.Platform.Audit;

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
    public Task RecordAsync(
        TenantId tenantId,
        string category,
        string action,
        string severity,
        string actorId,
        string actorType,
        string? targetId = null,
        string? targetType = null,
        Guid? correlationId = null,
        AuditChanges? changes = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorType);

        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            Action = action,
            Category = category,
            Severity = severity,
            ActorId = actorId,
            ActorType = actorType,
            TargetId = targetId,
            TargetType = targetType,
            CorrelationId = correlationId,
            Changes = changes,
            Metadata = metadata,
            OccurredAt = _clock.UtcNow,
        };

        return _store.SaveAsync(entry, ct);
    }

    /// <inheritdoc />
#pragma warning disable CS0618 // obsolete member used intentionally for backward compat
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

        return RecordAsync(
            tenantId,
            category: InferCategory(action),
            action: action,
            severity: "info",
            actorId: performedBy ?? "system",
            actorType: performedBy is null ? "system" : "user",
            targetId: entityId,
            targetType: entityType,
            metadata: details,
            ct: ct);
    }
#pragma warning restore CS0618

    private static string InferCategory(string action) => action switch
    {
        _ when action.StartsWith("login", StringComparison.Ordinal)
            || action.StartsWith("logout", StringComparison.Ordinal)
            || action.StartsWith("mfa", StringComparison.Ordinal)
            || action.StartsWith("session", StringComparison.Ordinal)
            || action.StartsWith("lockout", StringComparison.Ordinal) => "auth",
        _ when action.StartsWith("role", StringComparison.Ordinal)
            || action.StartsWith("permission", StringComparison.Ordinal) => "rbac",
        _ when action.StartsWith("recording", StringComparison.Ordinal)
            || action.StartsWith("transcript", StringComparison.Ordinal) => "data_access",
        _ when action.StartsWith("api_key", StringComparison.Ordinal)
            || action.StartsWith("tenant", StringComparison.Ordinal)
            || action.StartsWith("license", StringComparison.Ordinal)
            || action.StartsWith("system", StringComparison.Ordinal) => "admin",
        _ => "config",
    };
}
