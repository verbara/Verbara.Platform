using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public sealed class ConversationSession : IConversationSession
{
    public required EntityId SessionId { get; init; }
    public required ChannelType Channel { get; init; }
    public SessionState State { get; private set; } = SessionState.Active;
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; private set; }

    public void End(DateTimeOffset endedAt)
    {
        if (State == SessionState.Ended)
            throw new InvalidOperationException("Session is already ended.");
        State = SessionState.Ended;
        EndedAt = endedAt;
    }
}
