using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// A node in the cascading taxonomy. Only leaves are selectable as the final
/// outcome; depth = length of the parent chain (validated ≤ <c>MaxDepth</c> on publish).
/// </summary>
public sealed record TypificationNode
{
    public required EntityId NodeId { get; init; }

    /// <summary>Null = root level.</summary>
    public EntityId? ParentNodeId { get; init; }

    /// <summary>Localized key or literal (i18n via Web).</summary>
    public required string Label { get; init; }

    /// <summary>Stable reporting code, unique within the schema.</summary>
    public required string Code { get; init; }

    public int SortOrder { get; init; }

    /// <summary>Only leaves are selectable as the final outcome.</summary>
    public bool IsLeaf { get; init; }

    /// <summary>Null = applies to all channels.</summary>
    public IReadOnlyList<ChannelType>? ChannelApplicability { get; init; }

    /// <summary>Leaf-only outcome semantics; non-null iff <see cref="IsLeaf"/>.</summary>
    public LeafOutcome? Leaf { get; init; }
}
