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

    public TimeSpan Duration { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}
