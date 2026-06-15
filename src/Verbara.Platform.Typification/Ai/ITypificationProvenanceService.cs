using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Derives server-authoritative AI provenance for a typification submission and
/// records the correction signal (what the AI suggested vs. what the agent committed).
/// </summary>
/// <remarks>
/// Extracted from the typify handler (B3) to reduce handler parameter count and
/// keep provenance derivation + reconcile side-effects in a single testable unit.
/// </remarks>
public interface ITypificationProvenanceService
{
    /// <summary>
    /// Given the <paramref name="committedLeafNodeId"/> the agent just selected, fetches
    /// the latest stored suggestion for the conversation and returns derived provenance.
    /// As a side-effect (when a suggestion exists) it calls
    /// <see cref="IAiSuggestionStore.MarkReconciledAsync"/> and increments the appropriate
    /// <see cref="TypificationAiMetrics"/> counter.
    /// </summary>
    /// <remarks>
    /// <paramref name="tenantId"/> is <see cref="TenantId"/> (the handler type); conversion
    /// to <see cref="EntityId"/> (the store's tenant key type) is performed internally.
    /// </remarks>
    Task<ProvenanceResult> DeriveAsync(
        TenantId tenantId,
        EntityId conversationId,
        EntityId committedLeafNodeId,
        CancellationToken ct);
}

/// <summary>
/// Immutable result returned by <see cref="ITypificationProvenanceService.DeriveAsync"/>.
/// All fields are server-derived — never sourced from the client request body.
/// </summary>
public sealed record ProvenanceResult
{
    public bool AiSuggested { get; init; }
    public double? AiConfidence { get; init; }
    public bool? AiAccepted { get; init; }
    public SubmissionSource Source { get; init; }
    public EntityId? SuggestedLeafNodeId { get; init; }
    public IReadOnlyList<string>? SuggestedNodePath { get; init; }

    // ── AI model provenance (B4b — populated only when AiSuggested = true) ──────

    /// <summary>Model identifier from the stored suggestion (e.g. "gpt-4o-mini").</summary>
    public string? ModelId { get; init; }

    /// <summary>Prompt template version from the stored suggestion.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>Schema version at suggestion time from the stored suggestion.</summary>
    public int? SchemaVersion { get; init; }
}
