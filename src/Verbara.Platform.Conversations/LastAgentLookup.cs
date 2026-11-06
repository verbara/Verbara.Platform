namespace Verbara.Platform.Conversations;

/// <summary>
/// The one definition both conversation stores read for <see cref="IConversationStore.FindLastAgentOwnerAsync"/>
/// (conversations-return-to-last-agent design D4, ruling Q3).
/// </summary>
public static class LastAgentLookup
{
    /// <summary>
    /// The states that never name a previous agent: a merged conversation lives on in another, and spam is not a
    /// customer. Every other state counts, the open ones included. An exclusion, not a list of terminal states:
    /// <c>Abandoned</c> needs no entry, since no abandoned conversation is ever agent-owned.
    /// </summary>
    public static readonly IReadOnlyList<ConversationState> ExcludedStates = [ConversationState.Merged, ConversationState.Spam];
}
