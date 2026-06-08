using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// Conditional, structured capture field. Shown when its <see cref="AttachToNodeId"/>
/// branch is in the selected path (null = schema-global) and its
/// <see cref="VisibleWhen"/> rule evaluates true.
/// </summary>
public sealed record TypificationField
{
    public required EntityId FieldId { get; init; }

    /// <summary>Stable, unique within the schema; used in submissions and conditions.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required FieldType Type { get; init; }

    public bool Required { get; init; }

    /// <summary>Static options for Select/MultiSelect, or sourced via <see cref="OptionsDataDipRef"/>.</summary>
    public IReadOnlyList<FieldOption>? Options { get; init; }

    public FieldValidation? Validation { get; init; }

    /// <summary>Field shown when this node/branch is in the selected path; null = schema-global.</summary>
    public EntityId? AttachToNodeId { get; init; }

    /// <summary>Additional show/hide rule ("respuestas entrelazadas").</summary>
    public ConditionExpr? VisibleWhen { get; init; }

    /// <summary>Pre-fill source: metadata key, AI entity name, or data-dip ref (P2/P3).</summary>
    public PrefillRef? PrefillSource { get; init; }

    /// <summary>P3: populate <see cref="Options"/> dynamically from a data-dip.</summary>
    public string? OptionsDataDipRef { get; init; }

    public int SortOrder { get; init; }
}
