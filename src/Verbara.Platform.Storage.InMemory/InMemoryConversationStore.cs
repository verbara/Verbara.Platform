using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Storage.InMemory;

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
        // Mirror the Postgres partial-unique index uq_conversations_voice_linked_id:
        // a second conversation with the same (tenant, voice_linked_id) but a DIFFERENT
        // conversation_id is rejected as already-tracked (no-op), so InMemory and Postgres
        // agree on per-call voice idempotency (Phase-2.1 parity lesson). A re-save of the
        // SAME conversation_id is a normal upsert.
        if (conversation.VoiceLinkedId is { } linkedId)
        {
            var existing = _items.Values.FirstOrDefault(c =>
                c.TenantId == conversation.TenantId &&
                c.VoiceLinkedId == linkedId &&
                c.ConversationId != conversation.ConversationId);
            if (existing is not null)
                return Task.CompletedTask;
        }

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

    public Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.VoiceLinkedId == voiceLinkedId);

        return Task.FromResult(result);
    }

    public Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c => c.VoiceLinkedId == voiceLinkedId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId)
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        var toDelete = _items
            .Where(kv => kv.Key.Item1 == tenantId && kv.Value.ContactId == contactId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var toDelete = _items
            .Where(kv => kv.Key.Item1 == tenantId && kv.Value.CreatedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.State == ConversationState.Queued)
            .OrderBy(c => c.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.State == state)
            .OrderBy(c => c.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var count = _items.Values.Count(c =>
            c.TenantId == tenantId &&
            c.Owner is { Kind: ConversationOwnerKind.Agent } owner &&
            owner.OwnerId == agentId &&
            ConversationStateMachine.IsActiveWork(c.State));
        return Task.FromResult(count);
    }
}
