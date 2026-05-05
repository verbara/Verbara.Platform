using System.Text.Json.Serialization;

namespace Verbara.Platform.Api.Endpoints.Audit;

/// <summary>
/// Wire shape returned by the R5.2 PB.1 audit log viewer endpoints. Mirrors
/// <see cref="Verbara.Platform.Audit.AuditEntry"/> with snake-case JSON
/// nullable-everywhere so the React DataTable can render rows directly.
/// </summary>
/// <remarks>
/// The legacy <c>GET /admin/audit</c> endpoint keeps returning
/// <c>PagedResult&lt;AuditEntry&gt;</c> (entry shape) for back-compat with the
/// older audit page. The new endpoints (<c>/admin/audit/events</c> +
/// <c>/admin/audit/export</c>) return / stream this DTO.
/// </remarks>
public sealed record AuditEventDto
{
    [JsonPropertyName("entryId")] public required string EntryId { get; init; }
    [JsonPropertyName("tenantId")] public required string TenantId { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("actorId")] public required string ActorId { get; init; }
    [JsonPropertyName("actorType")] public required string ActorType { get; init; }
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
    [JsonPropertyName("targetType")] public string? TargetType { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
    [JsonPropertyName("impersonatorId")] public string? ImpersonatorId { get; init; }
    [JsonPropertyName("occurredAt")] public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Status surface — currently mirrors <see cref="Severity"/> until
    /// the entry model gains a dedicated outcome field. The Web table uses it as
    /// a separate column to leave room for the future split.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>Before snapshot for mutation events. <c>null</c> when not a mutation
    /// or when the storage layer doesn't persist before/after (legacy Postgres rows).</summary>
    [JsonPropertyName("before")] public object? Before { get; init; }

    [JsonPropertyName("after")] public object? After { get; init; }

    [JsonPropertyName("metadata")] public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
