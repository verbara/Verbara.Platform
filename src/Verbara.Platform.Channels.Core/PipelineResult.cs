using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public sealed record PipelineResult(
    EntityId ConversationId,
    EntityId ContactId,
    EntityId MessageId,
    bool IsNewConversation);
