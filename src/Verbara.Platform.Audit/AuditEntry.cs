using Verbara.Platform.Core;

namespace Verbara.Platform.Audit;

/// <summary>
/// An immutable record of an action taken within a tenant.
/// </summary>
public sealed class AuditEntry : ITenantScoped
{
    /// <summary>Unique identifier for this audit entry.</summary>
    public required EntityId EntryId { get; init; }

    /// <inheritdoc />
    public required TenantId TenantId { get; init; }

    /// <summary>
    /// Action that occurred, e.g. "conversation.created", "login.success", "role.assigned".
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Audit category. Must be one of the values allowed by the
    /// audit_entries_category_check constraint (migration 021 + 034):
    /// "auth", "billing", "config", "tenant", "security", "impersonation",
    /// "retention", "data", "rbac", "data_access", "admin", "conversations",
    /// "queues", "reports", "operational", "license". A value outside this set
    /// is rejected by Postgres (23514) — keep the constraint and the call sites
    /// in sync when adding a new category.
    /// </summary>
    public string Category { get; init; } = "config";

    /// <summary>
    /// Severity: "info", "warning", or "critical".
    /// </summary>
    public string Severity { get; init; } = "info";

    /// <summary>
    /// Who performed the action — user ID, API key ID, or "system".
    /// </summary>
    public string ActorId { get; init; } = "system";

    /// <summary>
    /// Type of actor: "user", "api_key", "system", or "ai".
    /// The "ai" value is used for entries describing automated decisions made by the AI
    /// typification classifier (GDPR Art. 22 traceability — migration 006).
    /// </summary>
    public string ActorType { get; init; } = "system";

    /// <summary>
    /// Identifier of the entity the action was performed on (optional).
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// Type of the entity the action was performed on, e.g. "Conversation", "Role" (optional).
    /// </summary>
    public string? TargetType { get; init; }

    /// <summary>
    /// Groups related audit actions across a single request or workflow.
    /// </summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>
    /// Before/after snapshot for mutation events.
    /// </summary>
    public AuditChanges? Changes { get; init; }

    /// <summary>
    /// SHA-256 (hex) tamper-evidence hash computed by <see cref="DefaultAuditService"/> over the
    /// entry's canonical identifying fields. Non-null for all entries created from v1.4 onwards.
    /// This is a plain SHA-256 over the entry's canonical fields providing tamper-evidence against
    /// accidental modification or corruption; it is NOT an HMAC and does not defend against a
    /// database-level actor who can recompute the hash (that would require a managed secret, which
    /// is outside the current scope).
    /// </summary>
    public string? IntegrityHash { get; init; }

    /// <summary>
    /// When set, indicates the action was performed during an impersonation session.
    /// Contains the user ID of the admin who initiated the impersonation.
    /// </summary>
    public string? ImpersonatorId { get; init; }

    /// <summary>Optional key/value metadata providing additional context for the action.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>When the action occurred.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    // ─── Backward-compatible aliases ──────────────────────────────────────────

    /// <summary>
    /// Alias for <see cref="TargetType"/>. Setting this initialises <see cref="TargetType"/>;
    /// getting it returns <see cref="TargetType"/> ?? "".
    /// </summary>
    [Obsolete("Use TargetType. This alias is kept for backward compatibility.")]
    public string EntityType
    {
        get => TargetType ?? string.Empty;
        init => TargetType = value;
    }

    /// <summary>
    /// Alias for <see cref="TargetId"/>. Setting this initialises <see cref="TargetId"/>;
    /// getting it returns <see cref="TargetId"/> ?? "".
    /// </summary>
    [Obsolete("Use TargetId. This alias is kept for backward compatibility.")]
    public string EntityId
    {
        get => TargetId ?? string.Empty;
        init => TargetId = value;
    }

    /// <summary>
    /// Alias for <see cref="ActorId"/>. Setting this initialises <see cref="ActorId"/>;
    /// getting it returns <see cref="ActorId"/>.
    /// </summary>
    [Obsolete("Use ActorId. This alias is kept for backward compatibility.")]
    public string? PerformedBy
    {
        get => ActorId == "system" ? null : ActorId;
        init => ActorId = value ?? "system";
    }

    /// <summary>
    /// Alias for <see cref="Metadata"/>. Setting this initialises <see cref="Metadata"/>;
    /// getting it returns <see cref="Metadata"/>.
    /// </summary>
    [Obsolete("Use Metadata. This alias is kept for backward compatibility.")]
    public IReadOnlyDictionary<string, string>? Details
    {
        get => Metadata;
        init => Metadata = value;
    }
}

/// <summary>
/// Captures before/after state for mutation audit events.
/// </summary>
public sealed record AuditChanges(object? Before, object? After);
