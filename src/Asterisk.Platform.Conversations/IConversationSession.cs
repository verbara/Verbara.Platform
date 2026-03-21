using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public interface IConversationSession
{
    EntityId SessionId { get; }
    ChannelType Channel { get; }
    SessionState State { get; }
    DateTimeOffset StartedAt { get; }
    DateTimeOffset? EndedAt { get; }
}
