using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryWrapUpStore : IWrapUpStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), WrapUpRecord> _items = new();

    public Task SaveAsync(WrapUpRecord record, CancellationToken ct)
    {
        _items[(record.TenantId, record.ConversationId)] = record;
        return Task.CompletedTask;
    }

    public Task<WrapUpRecord?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, conversationId), out var item);
        return Task.FromResult(item);
    }
}
