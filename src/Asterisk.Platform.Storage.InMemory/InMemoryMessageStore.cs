using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Message> _items = new();

    public Task SaveAsync(Message message, CancellationToken ct)
    {
        _items[(message.TenantId, message.MessageId)] = message;
        return Task.CompletedTask;
    }

    public Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, messageId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct)
    {
        IReadOnlyList<Message> result = _items.Values
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult(result);
    }

    public Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct)
    {
        if (_items.TryGetValue((tenantId, messageId), out var message))
        {
            message.DeliveryStatus = status;

            if (timestamp.HasValue)
            {
                if (status == MessageDeliveryStatus.Delivered)
                    message.DeliveredAt = timestamp;
                else if (status == MessageDeliveryStatus.Read)
                    message.ReadAt = timestamp;
            }
        }

        return Task.CompletedTask;
    }

    public Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.ExternalMessageId == externalMessageId);

        return Task.FromResult(result);
    }
}
