using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public sealed record ConversationOwner(ConversationOwnerKind Kind, EntityId? OwnerId = null)
{
    public static ConversationOwner System => new(ConversationOwnerKind.System);
    public static ConversationOwner ForBot(EntityId botId) => new(ConversationOwnerKind.Bot, botId);
    public static ConversationOwner ForAgent(EntityId agentId) => new(ConversationOwnerKind.Agent, agentId);
    public static ConversationOwner ForQueue(EntityId queueId) => new(ConversationOwnerKind.Queue, queueId);
}
