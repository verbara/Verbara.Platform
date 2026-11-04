using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// One item of <c>GET /api/v1/contacts/{id}/conversations</c> (contact-history-opens-by-assignment, design D5, D6, D8;
/// fixture <c>contact-conversation-history.v1.json</c>). A member that is <see langword="null"/> here is absent from the
/// JSON (<c>WhenWritingNull</c>).
/// </summary>
/// <param name="Id">The conversation id.</param>
/// <param name="Channel">The channel, a PascalCase string.</param>
/// <param name="State">The state, any state, terminal included.</param>
/// <param name="CreatedAt">When the conversation was created.</param>
/// <param name="ClosedAt">When it closed; absent while it has no close time.</param>
/// <param name="DurationSeconds">
/// Whole seconds, floored and never below 0: <c>lastMessageAt - createdAt</c> when <paramref name="ClosedReason"/> is
/// present (a system close), else <c>closedAt - createdAt</c>; absent when <paramref name="ClosedAt"/> is.
/// </param>
/// <param name="ClosedReason">The system close reason (<c>_closedReason</c>); absent when a person closed it, or it is open.</param>
/// <param name="QueueId">The last queue it was placed in; absent when unknown.</param>
/// <param name="QueueName">That queue's current name; absent when unknown or deleted.</param>
/// <param name="AgentId">The owner's id when the owner is an agent.</param>
/// <param name="AgentDisplayName">That agent's display name; absent when the agent no longer exists.</param>
/// <param name="Disposition">The typification; absent when never typified.</param>
/// <param name="LastMessageAt">The newest activity message's time, else the stored last-activity time; always present.</param>
/// <param name="LastMessage">The newest activity message; absent when there is none.</param>
/// <param name="HeldByCaller">Whether the caller holds the conversation now (design D3).</param>
internal sealed record ContactConversationSummaryDto(
    string Id,
    ChannelType Channel,
    ConversationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    long? DurationSeconds,
    string? ClosedReason,
    string? QueueId,
    string? QueueName,
    string? AgentId,
    string? AgentDisplayName,
    ConversationDispositionDto? Disposition,
    DateTimeOffset LastMessageAt,
    ConversationLastMessageDto? LastMessage,
    bool HeldByCaller);

/// <summary>A summary's disposition: the leaf node, and its label when the node still resolves.</summary>
internal sealed record ConversationDispositionDto(string NodeId, string? Label);

/// <summary>A summary's last activity message: its direction, its first block's type, a text preview and its time.</summary>
internal sealed record ConversationLastMessageDto(
    MessageDirection Direction, MessageBlockType BlockType, string Preview, DateTimeOffset CreatedAt);

/// <summary>Maps a stored <see cref="ContactConversationSummary"/> to its wire shape (design D6).</summary>
internal static class ContactConversationSummaryMapper
{
    /// <summary>The most UTF-16 code units a preview carries.</summary>
    public const int PreviewLength = 160;

    /// <summary>The wire summary of <paramref name="summary"/>.</summary>
    /// <param name="summary">The stored row.</param>
    /// <param name="heldByCaller">The D3 flag, from <c>ConversationReader.IsHeldByCaller</c>.</param>
    /// <param name="dispositionLabel">The disposition node's label, when it resolved.</param>
    public static ContactConversationSummaryDto ToDto(
        ContactConversationSummary summary, bool heldByCaller, string? dispositionLabel)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var conversation = summary.Conversation;
        var lastMessageAt = LastMessageAt(summary);
        var agentId = conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId } ? ownerId.Value : null;

        return new ContactConversationSummaryDto(
            Id: conversation.ConversationId.Value,
            Channel: conversation.Channel,
            State: conversation.State,
            CreatedAt: conversation.CreatedAt,
            ClosedAt: conversation.ClosedAt,
            DurationSeconds: DurationSeconds(conversation, lastMessageAt),
            ClosedReason: ClosedReasonOf(conversation),
            QueueId: conversation.QueueId?.Value,
            QueueName: conversation.QueueId is null ? null : summary.QueueName,
            AgentId: agentId,
            AgentDisplayName: agentId is null ? null : summary.AgentDisplayName,
            Disposition: summary.Disposition is { } disposition
                ? new ConversationDispositionDto(disposition.NodeId.Value, dispositionLabel)
                : null,
            LastMessageAt: lastMessageAt,
            LastMessage: summary.LastMessage is { } message
                ? new ConversationLastMessageDto(
                    message.Direction,
                    message.Content.Blocks.Count > 0 ? message.Content.Blocks[0].Type : MessageBlockType.Text,
                    Preview(message.Content),
                    message.CreatedAt)
                : null,
            HeldByCaller: heldByCaller);
    }

    /// <summary>
    /// <c>COALESCE(newest activity message's time, the stored clock)</c>: the column alone is the creation time of a row
    /// closed before migration 026. A row with neither falls back to its creation.
    /// </summary>
    public static DateTimeOffset LastMessageAt(ContactConversationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.LastMessage?.CreatedAt ?? summary.Conversation.LastMessageAt ?? summary.Conversation.CreatedAt;
    }

    /// <summary>
    /// The duration rule of design D6: with a system close reason, <paramref name="lastMessageAt"/> - created (the
    /// handling, not the silence before the close); otherwise closed - created. Whole seconds, floored, never below 0;
    /// <see langword="null"/> while the conversation has no close time.
    /// </summary>
    public static long? DurationSeconds(Conversation conversation, DateTimeOffset lastMessageAt)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.ClosedAt is not { } closedAt)
            return null;

        var end = ClosedReasonOf(conversation) is null ? closedAt : lastMessageAt;
        var seconds = (long)Math.Floor((end - conversation.CreatedAt).TotalSeconds);
        return Math.Max(0L, seconds);
    }

    /// <summary>
    /// The first block's text, trimmed and cut to <see cref="PreviewLength"/> UTF-16 code units without splitting a
    /// surrogate pair, or <c>""</c> when the first block is not text.
    /// </summary>
    public static string Preview(MessageEnvelope content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Blocks.Count == 0 || content.Blocks[0] is not TextBlock text)
            return string.Empty;

        var trimmed = text.Text.Trim();
        if (trimmed.Length <= PreviewLength)
            return trimmed;

        // A cut between a high and a low surrogate would leave half a character: drop the whole pair.
        var length = char.IsHighSurrogate(trimmed[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
        return trimmed[..length];
    }

    private static string? ClosedReasonOf(Conversation conversation) =>
        conversation.Metadata.TryGetValue(ConversationCloseReasons.MetadataKey, out var reason) && !string.IsNullOrEmpty(reason)
            ? reason
            : null;
}
