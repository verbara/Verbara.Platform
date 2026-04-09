using Asterisk.Platform.Core;

namespace Asterisk.Platform.Switchboard;

public interface IConversationSwitchboard
{
    Task<OwnershipResult> AssignToQueueAsync(EntityId conversationId, TenantId tenantId, EntityId queueId, CancellationToken ct);
    Task<OwnershipResult> OfferToAgentAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> AcceptAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> RejectAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> TransferToQueueAsync(EntityId conversationId, TenantId tenantId, EntityId targetQueueId, CancellationToken ct);
    Task<OwnershipResult> TransferToAgentAsync(EntityId conversationId, TenantId tenantId, EntityId targetAgentId, CancellationToken ct);
    Task<OwnershipResult> ReturnToBotAsync(EntityId conversationId, TenantId tenantId, EntityId botId, CancellationToken ct);
    Task<OwnershipResult> HoldAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<OwnershipResult> UnholdAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
}
