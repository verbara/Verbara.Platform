using System.Collections.Concurrent;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

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
