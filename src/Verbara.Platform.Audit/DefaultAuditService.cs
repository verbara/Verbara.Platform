using System.Security.Cryptography;
using System.Text;
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

        var occurredAt = _clock.UtcNow;

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
            OccurredAt = occurredAt,
            IntegrityHash = ComputeIntegrityHash(tenantId, actorType, actorId, action, targetType, targetId, occurredAt, metadata),
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

    /// <summary>
    /// Computes a deterministic SHA-256 (hex) integrity hash over the entry's canonical
    /// identifying fields. Metadata keys are sorted alphabetically so the hash is stable
    /// regardless of insertion order. If a field is null it contributes an empty segment
    /// so the canonical form never collides across field positions.
    /// </summary>
    private static string ComputeIntegrityHash(
        TenantId tenantId,
        string actorType,
        string actorId,
        string action,
        string? targetType,
        string? targetId,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string>? metadata)
    {
        // Canonical form: pipe-delimited, fixed field order, metadata as sorted k=v pairs.
        var sb = new StringBuilder(256);
        sb.Append(tenantId.Value);
        sb.Append('|');
        sb.Append(actorType);
        sb.Append('|');
        sb.Append(actorId);
        sb.Append('|');
        sb.Append(action);
        sb.Append('|');
        sb.Append(targetType ?? string.Empty);
        sb.Append('|');
        sb.Append(targetId ?? string.Empty);
        sb.Append('|');
        sb.Append(occurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        if (metadata is { Count: > 0 })
        {
            var keys = new string[metadata.Count];
            var i = 0;
            foreach (var k in metadata.Keys)
                keys[i++] = k;
            Array.Sort(keys, StringComparer.Ordinal);

            sb.Append('|');
            var first = true;
            foreach (var k in keys)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(k);
                sb.Append('=');
                sb.Append(metadata[k]);
            }
        }

        var input = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(input);
        return Convert.ToHexStringLower(hash);
    }

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
