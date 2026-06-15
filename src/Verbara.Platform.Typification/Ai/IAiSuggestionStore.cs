using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Persistence abstraction for AI suggestion shadow records.
/// Implementations: <c>InMemoryAiSuggestionStore</c> (dev/test),
/// <c>PostgresAiSuggestionStore</c> (production).
/// </summary>
public interface IAiSuggestionStore
{
    /// <summary>Inserts a new suggestion record.</summary>
    Task SaveAsync(AiSuggestionRecord record, CancellationToken ct);

    /// <summary>
    /// Returns the most recent suggestion (by <c>created_at</c>) for the given conversation,
    /// or <see langword="null"/> if none exists.
    /// </summary>
    Task<AiSuggestionRecord?> GetLatestForConversationAsync(
        EntityId tenantId,
        EntityId conversationId,
        CancellationToken ct);

    /// <summary>
    /// Stamps a suggestion with the agent's actual committed leaf node and whether they accepted
    /// the AI suggestion. Safe to call more than once — a subsequent call overwrites the prior
    /// reconciliation (the typify path in B3 is the single writer per conversation).
    /// </summary>
    Task MarkReconciledAsync(
        EntityId id,
        EntityId committedLeafNodeId,
        bool accepted,
        CancellationToken ct);

    /// <summary>
    /// Returns accuracy statistics for reconciled suggestions (those where <c>Accepted</c> is
    /// not <see langword="null"/>) whose <c>Confidence</c> is ≥ <paramref name="confidenceThreshold"/>.
    /// </summary>
    /// <returns>
    /// <c>Samples</c>: number of reconciled rows above the threshold.
    /// <c>AcceptRate</c>: fraction accepted (0 when <c>Samples == 0</c>).
    /// </returns>
    Task<(int Samples, double AcceptRate)> QueryAccuracyAsync(
        EntityId tenantId,
        EntityId schemaId,
        double confidenceThreshold,
        CancellationToken ct);
}
