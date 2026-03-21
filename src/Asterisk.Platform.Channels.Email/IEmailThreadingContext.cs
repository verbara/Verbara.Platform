using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Email;

/// <summary>Holds RFC email threading headers for an outbound email reply.</summary>
public sealed record EmailThreadInfo(string? InReplyTo, string? References);

/// <summary>
/// Provides email threading header information (<c>In-Reply-To</c>, <c>References</c>)
/// for outbound messages keyed by conversation ID.
/// </summary>
public interface IEmailThreadingContext
{
    /// <summary>Returns threading info for the given conversation, or <c>null</c> if not a reply.</summary>
    EmailThreadInfo? GetThreadInfo(EntityId conversationId);

    /// <summary>Records that an outbound message was sent, so future replies can thread correctly.</summary>
    void RecordSentMessage(EntityId conversationId, string messageId);
}
