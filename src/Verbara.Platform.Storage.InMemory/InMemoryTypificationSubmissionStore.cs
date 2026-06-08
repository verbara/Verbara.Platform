using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryTypificationSubmissionStore : ITypificationSubmissionStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), TypificationSubmission> _items = new();

    public Task SaveAsync(TypificationSubmission submission, CancellationToken ct)
    {
        _items[(submission.TenantId, submission.ConversationId)] = submission;
        return Task.CompletedTask;
    }

    public Task<TypificationSubmission?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, conversationId), out var item);
        return Task.FromResult(item);
    }
}
