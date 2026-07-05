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
        DateTimeOffset? retainUntil = null,
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
            RetainUntil = retainUntil,
            // audit-trail-integrity-fixes (fix 4): every NEWLY written entry uses the v2 scheme,
            // which covers RetainUntil (a retention-date mutation is otherwise undetectable by hash
            // verification). Pre-existing rows keep verifying under v1 — see VerifyIntegrity below.
            IntegrityHash = ComputeIntegrityHashV2(tenantId, actorType, actorId, action, targetType, targetId, occurredAt, retainUntil, metadata),
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
    /// The stored-hash prefix identifying the v2 scheme (audit-trail-integrity-fixes, fix 4).
    /// A hash string with this prefix covers <see cref="AuditEntry.RetainUntil"/>; a bare hex
    /// string with no prefix is a v1-scheme hash (pre-existing rows), which does NOT.
    /// </summary>
    public const string HashSchemeV2Prefix = "v2:";

    /// <summary>
    /// Computes a deterministic SHA-256 (hex) integrity hash over the entry's canonical
    /// identifying fields (the v1 scheme: tenant/actor/action/target/occurredAt/metadata — does
    /// NOT cover <see cref="AuditEntry.RetainUntil"/>). Metadata keys are sorted alphabetically
    /// (Ordinal) so the hash is stable regardless of insertion order. If a field is null it
    /// contributes an empty segment so the canonical form never collides across field positions.
    /// Kept for pre-existing rows and callers written before the v2 scheme
    /// (audit-trail-integrity-fixes, fix 4) — new writes use <see cref="ComputeIntegrityHashV2"/>.
    /// </summary>
    public static string ComputeIntegrityHash(
        TenantId tenantId,
        string actorType,
        string actorId,
        string action,
        string? targetType,
        string? targetId,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var sb = new StringBuilder(256);
        AppendV1Fields(sb, tenantId, actorType, actorId, action, targetType, targetId, occurredAt);
        AppendMetadata(sb, metadata);
        return HashHex(sb);
    }

    /// <summary>
    /// Computes the v2-scheme integrity hash: the v1 canonical fields PLUS
    /// <see cref="AuditEntry.RetainUntil"/> (audit-trail-integrity-fixes, fix 4) — so a retention-date
    /// mutation on a v2-scheme entry is detectable, closing the gap where <c>RetainUntil</c> sat
    /// outside the hash's coverage. The returned string is prefixed with
    /// <see cref="HashSchemeV2Prefix"/> so <see cref="VerifyIntegrity"/> can tell a v2 row from a
    /// pre-existing v1 row without a separate schema column. <c>RetainUntil</c> is inserted as its
    /// own pipe-delimited segment BETWEEN <c>occurredAt</c> and the metadata block (an empty segment
    /// when null) so the position — not just the value — is covered, exactly like every other
    /// nullable field in the v1 canonical form.
    /// </summary>
    public static string ComputeIntegrityHashV2(
        TenantId tenantId,
        string actorType,
        string actorId,
        string action,
        string? targetType,
        string? targetId,
        DateTimeOffset occurredAt,
        DateTimeOffset? retainUntil,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var sb = new StringBuilder(256);
        AppendV1Fields(sb, tenantId, actorType, actorId, action, targetType, targetId, occurredAt);
        sb.Append('|');
        sb.Append(retainUntil?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        AppendMetadata(sb, metadata);
        return HashSchemeV2Prefix + HashHex(sb);
    }

    /// <summary>
    /// Verifies an entry's <see cref="AuditEntry.IntegrityHash"/> against its current field values,
    /// dispatching on the stored hash's scheme (audit-trail-integrity-fixes, fix 4): a
    /// <see cref="HashSchemeV2Prefix"/>-prefixed hash is re-derived (and compared) under the v2
    /// scheme (covers <see cref="AuditEntry.RetainUntil"/>); a bare-hex hash is re-derived under the
    /// v1 scheme, so pre-existing rows keep verifying unchanged after the scheme change. A
    /// <see langword="null"/> or empty stored hash never verifies (nothing to compare against).
    /// </summary>
    public static bool VerifyIntegrity(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrEmpty(entry.IntegrityHash))
            return false;

        if (entry.IntegrityHash.StartsWith(HashSchemeV2Prefix, StringComparison.Ordinal))
        {
            var expectedV2 = ComputeIntegrityHashV2(
                entry.TenantId, entry.ActorType, entry.ActorId, entry.Action,
                entry.TargetType, entry.TargetId, entry.OccurredAt, entry.RetainUntil, entry.Metadata);
            return string.Equals(entry.IntegrityHash, expectedV2, StringComparison.Ordinal);
        }

        var expectedV1 = ComputeIntegrityHash(
            entry.TenantId, entry.ActorType, entry.ActorId, entry.Action,
            entry.TargetType, entry.TargetId, entry.OccurredAt, entry.Metadata);
        return string.Equals(entry.IntegrityHash, expectedV1, StringComparison.Ordinal);
    }

    // Canonical form: pipe-delimited fixed fields. Every variable string segment is
    // percent-encoded with Uri.EscapeDataString so that values containing the delimiters
    // ('|', ',', '=') cannot produce ambiguous canonical strings, making the hash
    // collision-resistant for arbitrary metadata keys and values. Shared by both schemes — v2
    // inserts its extra RetainUntil segment immediately after, before metadata is appended.
    private static void AppendV1Fields(
        StringBuilder sb,
        TenantId tenantId,
        string actorType,
        string actorId,
        string action,
        string? targetType,
        string? targetId,
        DateTimeOffset occurredAt)
    {
        sb.Append(Uri.EscapeDataString(tenantId.Value));
        sb.Append('|');
        sb.Append(Uri.EscapeDataString(actorType));
        sb.Append('|');
        sb.Append(Uri.EscapeDataString(actorId));
        sb.Append('|');
        sb.Append(Uri.EscapeDataString(action));
        sb.Append('|');
        sb.Append(Uri.EscapeDataString(targetType ?? string.Empty));
        sb.Append('|');
        sb.Append(Uri.EscapeDataString(targetId ?? string.Empty));
        sb.Append('|');
        sb.Append(occurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendMetadata(StringBuilder sb, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is not { Count: > 0 })
            return;

        var keys = new string[metadata.Count];
        var i = 0;
        foreach (var k in metadata.Keys)
            keys[i++] = k;
        // Sort before encoding so the sort order is based on the raw key strings,
        // matching the stable Ordinal ordering a caller would expect.
        Array.Sort(keys, StringComparer.Ordinal);

        sb.Append('|');
        var first = true;
        foreach (var k in keys)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Uri.EscapeDataString(k));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(metadata[k]));
        }
    }

    private static string HashHex(StringBuilder sb)
    {
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
