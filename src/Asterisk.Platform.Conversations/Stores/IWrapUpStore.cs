using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Stores;

public interface IWrapUpStore
{
    Task SaveAsync(WrapUpRecord record, CancellationToken ct);
    Task<WrapUpRecord?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
}
