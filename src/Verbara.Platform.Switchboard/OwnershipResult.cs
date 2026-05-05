using Verbara.Platform.Conversations;

namespace Verbara.Platform.Switchboard;

public sealed record OwnershipResult(
    bool Success,
    ConversationOwner? NewOwner,
    ConversationState NewState,
    string? FailureReason);
