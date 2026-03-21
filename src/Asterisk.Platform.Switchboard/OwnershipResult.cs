using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Switchboard;

public sealed record OwnershipResult(
    bool Success,
    ConversationOwner? NewOwner,
    ConversationState NewState,
    string? FailureReason);
