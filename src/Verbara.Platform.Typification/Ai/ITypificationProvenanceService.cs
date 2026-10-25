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

    /// <summary>
    /// Reads the latest stored suggestion for the conversation and returns ONLY the derived
    /// <see cref="SubmissionSource"/> (AutoAi when the committed leaf matches the suggestion,
    /// else Manual). Pure read — does NOT reconcile the suggestion or touch metrics. Used to
    /// make the /typify validation + PII-screen Source-aware BEFORE the (mutating) reconcile.
    /// </summary>
    Task<SubmissionSource> DeriveSourceAsync(TenantId tenantId, EntityId conversationId, EntityId committedLeafNodeId, CancellationToken ct);

    /// <summary>
    /// The derivation half of <see cref="DeriveAsync"/>: reads the latest stored suggestion and returns the provenance
    /// it gives <paramref name="committedLeafNodeId"/>, with the suggestion's id. A pure read: it does NOT reconcile
    /// the suggestion and touches no metric (design D15: the voice path derives before its insert-or-nothing write and
    /// reconciles only when that write stored the submission).
    /// </summary>
    Task<ProvenanceResult> ReadAsync(
        TenantId tenantId,
        EntityId conversationId,
        EntityId committedLeafNodeId,
        CancellationToken ct);

    /// <summary>
    /// The side-effect half of <see cref="DeriveAsync"/>: when <paramref name="provenance"/> names a suggestion, marks
    /// it reconciled with <paramref name="committedLeafNodeId"/> (<see cref="IAiSuggestionStore.MarkReconciledAsync"/>)
    /// and counts the accept or the override once. Does nothing when no suggestion was involved.
    /// </summary>
    Task ReconcileAsync(ProvenanceResult provenance, EntityId committedLeafNodeId, CancellationToken ct);
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

    /// <summary>The id of the stored suggestion the provenance was derived from (null when none exists).</summary>
    public EntityId? SuggestionId { get; init; }

    // ── AI model provenance (B4b — populated only when AiSuggested = true) ──────

    /// <summary>Model identifier from the stored suggestion (e.g. "gpt-4o-mini").</summary>
    public string? ModelId { get; init; }

    /// <summary>Prompt template version from the stored suggestion.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>Schema version at suggestion time from the stored suggestion.</summary>
    public int? SchemaVersion { get; init; }
}
