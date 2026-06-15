using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Default implementation of <see cref="ITypificationProvenanceService"/>.
/// Registered as a singleton in <c>AddPlatformTypification()</c>.
/// </summary>
internal sealed class DefaultTypificationProvenanceService : ITypificationProvenanceService
{
    private readonly IAiSuggestionStore _suggestionStore;
    private readonly TypificationAiMetrics _metrics;

    public DefaultTypificationProvenanceService(
        IAiSuggestionStore suggestionStore,
        TypificationAiMetrics metrics)
    {
        _suggestionStore = suggestionStore;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<ProvenanceResult> DeriveAsync(
        TenantId tenantId,
        EntityId conversationId,
        EntityId committedLeafNodeId,
        CancellationToken ct)
    {
        // IAiSuggestionStore uses EntityId for tenant (consistent with B2 SaveAsync path).
        var tenantEntityId = EntityId.From(tenantId.Value);

        var suggestion = await _suggestionStore.GetLatestForConversationAsync(
            tenantEntityId, conversationId, ct);

        if (suggestion is null)
        {
            return new ProvenanceResult
            {
                AiSuggested = false,
                AiConfidence = null,
                AiAccepted = null,
                Source = SubmissionSource.Manual,
                SuggestedLeafNodeId = null,
                SuggestedNodePath = null,
            };
        }

        var aiAccepted = suggestion.SuggestedLeafNodeId == committedLeafNodeId;

        await _suggestionStore.MarkReconciledAsync(
            suggestion.Id, committedLeafNodeId, aiAccepted, ct);

        if (aiAccepted)
            _metrics.SuggestionAccepted.Add(1);
        else
            _metrics.SuggestionOverridden.Add(1);

        return new ProvenanceResult
        {
            AiSuggested = true,
            AiConfidence = suggestion.Confidence,
            AiAccepted = aiAccepted,
            Source = aiAccepted ? SubmissionSource.AutoAi : SubmissionSource.Manual,
            SuggestedLeafNodeId = suggestion.SuggestedLeafNodeId,
            SuggestedNodePath = suggestion.SuggestedNodePath,
            ModelId = suggestion.ModelId,
            PromptVersion = suggestion.PromptVersion,
            SchemaVersion = suggestion.SchemaVersion,
        };
    }
}
