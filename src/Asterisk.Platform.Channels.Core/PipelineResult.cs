using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core;

public sealed record PipelineResult(
    EntityId ConversationId,
    EntityId ContactId,
    EntityId MessageId,
    bool IsNewConversation);
