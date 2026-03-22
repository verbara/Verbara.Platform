using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Conversation> _items = new();

    public Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, conversationId), out var item);
        return Task.FromResult(item);
    }

    public Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(c => c.TenantId == tenantId)
            .Where(c => query.State == null || c.State == query.State)
            .Where(c => query.ContactId == null || c.ContactId == query.ContactId)
            .Where(c => query.CaseId == null || c.CaseId == query.CaseId)
            .Where(c => query.AssignedAgentId == null || (c.Owner != null && c.Owner.OwnerId == query.AssignedAgentId))
            .Where(c => query.Channel == null || c.Channel == query.Channel)
            .ToList();

        var totalCount = filtered.Count;
        var offset = (query.Page - 1) * query.PageSize;
        var items = filtered.Skip(offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Conversation>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Conversation conversation, CancellationToken ct)
    {
        _items[(conversation.TenantId, conversation.ConversationId)] = conversation;
        return Task.CompletedTask;
    }

    public Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.ContactId == contactId &&
            c.Channel == channel &&
            !ConversationStateMachine.IsTerminal(c.State));

        return Task.FromResult(result);
    }
}
