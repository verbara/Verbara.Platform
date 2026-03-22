using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit;

/// <summary>
/// An immutable record of an action taken on an entity within a tenant.
/// </summary>
public sealed class AuditEntry : ITenantScoped
{
    /// <summary>Unique identifier for this audit entry.</summary>
    public required EntityId EntryId { get; init; }

    /// <inheritdoc />
    public required TenantId TenantId { get; init; }

    /// <summary>
    /// Action that occurred, e.g. "conversation.created", "message.sent", "agent.state_changed".
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Type of the entity the action was performed on, e.g. "Conversation", "Message", "Agent".
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>Identifier of the entity the action was performed on.</summary>
    public required string EntityId { get; init; }

    /// <summary>User identifier or "system" if performed by an automated process.</summary>
    public string? PerformedBy { get; init; }

    /// <summary>Optional key/value metadata providing additional context for the action.</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    /// <summary>When the action occurred.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}
