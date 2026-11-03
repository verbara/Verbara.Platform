using System.Globalization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// conversations-close-after-inactivity (design D10; spec <c>conversation-queue-timeout</c>): when a conversation last
/// entered a queue. The save that puts it in a queue (a routing assignment, a transfer to a queue or a failover
/// re-queue) stamps <see cref="QueuedAtKey"/>; an expired or rejected offer keeps it, so the queue timeout counts the
/// whole wait and never from the conversation's creation.
/// </summary>
public static class ConversationQueueClock
{
    /// <summary>Metadata key holding when the conversation last entered a queue (round-trip "O" format, UTC).</summary>
    public const string QueuedAtKey = "_queuedAt";

    /// <summary>Records <paramref name="now"/> as the moment <paramref name="conversation"/> entered the queue.</summary>
    public static void Stamp(Conversation conversation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.SetMetadata(QueuedAtKey, now.ToUtcInstant().ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// When <paramref name="conversation"/> entered the queue: <see cref="QueuedAtKey"/>, or its creation when the key
    /// is absent or unparseable (a row queued before the key existed counts as it always did).
    /// </summary>
    public static DateTimeOffset EnteredQueueAt(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Metadata.TryGetValue(QueuedAtKey, out var stamp)
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var queuedAt)
                ? queuedAt.ToUtcInstant()
                : conversation.CreatedAt;
    }
}
