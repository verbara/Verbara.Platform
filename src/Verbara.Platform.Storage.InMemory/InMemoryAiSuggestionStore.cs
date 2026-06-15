using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryAiSuggestionStore : IAiSuggestionStore
{
    private readonly ConcurrentDictionary<string, AiSuggestionRecord> _items = new();

    public Task SaveAsync(AiSuggestionRecord record, CancellationToken ct)
    {
        _items[record.Id.Value] = record;
        return Task.CompletedTask;
    }

    public Task<AiSuggestionRecord?> GetLatestForConversationAsync(
        EntityId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var latest = _items.Values
            .Where(r => r.TenantId == tenantId && r.ConversationId == conversationId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task MarkReconciledAsync(
        EntityId id, EntityId committedLeafNodeId, bool accepted, CancellationToken ct)
    {
        if (_items.TryGetValue(id.Value, out var existing))
        {
            _items[id.Value] = new AiSuggestionRecord
            {
                Id = existing.Id,
                TenantId = existing.TenantId,
                ConversationId = existing.ConversationId,
                SchemaId = existing.SchemaId,
                SchemaVersion = existing.SchemaVersion,
                SuggestedLeafNodeId = existing.SuggestedLeafNodeId,
                SuggestedNodePath = existing.SuggestedNodePath,
                SuggestedFieldValues = existing.SuggestedFieldValues,
                Confidence = existing.Confidence,
                Sentiment = existing.Sentiment,
                ModelId = existing.ModelId,
                PromptVersion = existing.PromptVersion,
                SurfacedBand = existing.SurfacedBand,
                CreatedAt = existing.CreatedAt,
                CommittedLeafNodeId = committedLeafNodeId,
                Accepted = accepted,
            };
        }
        return Task.CompletedTask;
    }

    public Task<(int Samples, double AcceptRate)> QueryAccuracyAsync(
        EntityId tenantId, EntityId schemaId, int schemaVersion, double confidenceThreshold, CancellationToken ct)
    {
        var reconciled = _items.Values
            .Where(r =>
                r.TenantId == tenantId &&
                r.SchemaId == schemaId &&
                r.SchemaVersion == schemaVersion &&
                r.Accepted is not null &&
                r.Confidence >= confidenceThreshold &&
                r.SurfacedBand != TypificationBand.AutoFill)
            .ToList();

        if (reconciled.Count == 0)
            return Task.FromResult((0, 0d));

        var acceptedCount = reconciled.Count(r => r.Accepted == true);
        return Task.FromResult((reconciled.Count, (double)acceptedCount / reconciled.Count));
    }
}
