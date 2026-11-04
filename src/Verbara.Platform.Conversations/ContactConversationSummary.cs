using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

/// <summary>
/// One row of a contact's conversation history (contact-history-opens-by-assignment, design D5, D6, D8): the
/// conversation and what one summary query joins to it. The Api maps it to the wire summary; nothing here is a wire
/// shape.
/// </summary>
/// <param name="Conversation">The conversation as stored, its last queue (<see cref="Conversation.QueueId"/>) included.</param>
/// <param name="QueueName">
/// The current name of <see cref="Conversation.QueueId"/>'s queue; <see langword="null"/> when no queue is recorded or the
/// queue no longer exists.
/// </param>
/// <param name="AgentDisplayName">
/// The display name of the owner when its kind is <see cref="ConversationOwnerKind.Agent"/>; <see langword="null"/> for
/// any other owner, or when the agent no longer exists.
/// </param>
/// <param name="Disposition">The typification, the correction's leaf when one exists; <see langword="null"/> when never typified.</param>
/// <param name="LastMessage">
/// The newest activity message (an <c>Inbound</c> message, or an <c>Outbound</c> one not stored <c>Failed</c>; a
/// <c>System</c> message never counts), <see langword="null"/> when there is none.
/// </param>
public sealed record ContactConversationSummary(
    Conversation Conversation,
    string? QueueName,
    string? AgentDisplayName,
    ConversationDispositionRef? Disposition,
    ConversationActivityMessage? LastMessage);

/// <summary>
/// The disposition of a typified conversation as stored: the leaf node, taken from the supervisor's correction when one
/// exists (ADR-0034), else from the submission, and the schema version the submission was made against, which resolves
/// the node's label.
/// </summary>
public sealed record ConversationDispositionRef(EntityId NodeId, EntityId SchemaId, int SchemaVersion);

/// <summary>The newest activity message of a conversation, as much of it as a summary needs.</summary>
public sealed record ConversationActivityMessage(MessageDirection Direction, MessageEnvelope Content, DateTimeOffset CreatedAt);
