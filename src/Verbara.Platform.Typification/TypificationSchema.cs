using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// A per-tenant, versioned, publishable typification taxonomy. Definition
/// (nodes/fields/dataDips/aiConfig) persists as JSONB into these typed records;
/// versions are immutable once published (new edits => new version).
/// </summary>
public sealed record TypificationSchema : ITenantScoped
{
    public required EntityId SchemaId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; init; }

    /// <summary>Immutable once published; new edits => new version.</summary>
    public int Version { get; init; }

    public bool IsPublished { get; init; }

    /// <summary>Default 5, validated 1..8 on publish (D5).</summary>
    public int MaxDepth { get; init; } = 5;

    public required IReadOnlyList<TypificationNode> Nodes { get; init; }
    public required IReadOnlyList<TypificationField> Fields { get; init; }

    /// <summary>P3 — empty in P0.</summary>
    public required IReadOnlyList<DataDipDef> DataDips { get; init; }

    /// <summary>P2 — disabled in P0.</summary>
    public required TypificationAiConfig AiConfig { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
