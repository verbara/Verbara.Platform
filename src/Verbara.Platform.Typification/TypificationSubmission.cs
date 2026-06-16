using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// A completed typification for a conversation (replaces the disposition part of
/// the old WrapUpRecord). Field values are typed-validated server-side.
/// </summary>
public sealed record TypificationSubmission : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required EntityId AgentId { get; init; }
    public required EntityId SchemaId { get; init; }
    public int SchemaVersion { get; init; }

    /// <summary>root..leaf.</summary>
    public required IReadOnlyList<EntityId> SelectedNodePath { get; init; }

    public required EntityId LeafNodeId { get; init; }

    /// <summary>key → value (typed-validated server-side).</summary>
    public required IReadOnlyDictionary<string, string> FieldValues { get; init; }

    public string? Notes { get; init; }

    public bool AiSuggested { get; init; }
    public double? AiConfidence { get; init; }

    /// <summary>Did the agent keep the AI suggestion?</summary>
    public bool? AiAccepted { get; init; }

    public SubmissionSource Source { get; init; }

    /// <summary>
    /// The leaf node the AI suggested at classification time (null when no suggestion exists).
    /// Captured for correction-signal analysis (B3).
    /// </summary>
    public EntityId? SuggestedLeafNodeId { get; init; }

    /// <summary>
    /// Full node path (root→leaf) the AI suggested (null when no suggestion exists).
    /// Captured for correction-signal analysis (B3).
    /// </summary>
    public IReadOnlyList<string>? SuggestedNodePath { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}
