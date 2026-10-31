using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The note a transfer leaves for whoever takes the conversation: an internal <see cref="MessageDirection.System"/>
/// message of the conversation's own channel, stored after the move and published to the console.
/// </summary>
/// <remarks>
/// Best-effort, as the audit entry is: the move has happened when the note is written, so nothing here can fail the
/// request, and a client that hangs up does not lose the note. The audit is where a failed write stays visible, and
/// neither it nor a log ever holds the text, because a note may carry customer data. An exception is logged by its type
/// only, since a database error can quote the row.
/// </remarks>
internal static partial class ConversationTransferNote
{
    /// <summary>The longest note accepted, in characters after trimming.</summary>
    public const int MaxLength = 1000;

    /// <summary>The audit metadata key saying what happened to the note: <see cref="None"/>, <see cref="Written"/> or <see cref="Failed"/>.</summary>
    public const string AuditKey = "note";

    /// <summary>The transfer carried no note.</summary>
    public const string None = "none";

    /// <summary>The note is stored.</summary>
    public const string Written = "written";

    /// <summary>The note could not be stored; the transfer stands.</summary>
    public const string Failed = "failed";

    private const string LoggerCategory = "Verbara.Platform.Api.Endpoints.ConversationTransferNote";

    /// <summary>The note as it is stored: trimmed, or <see langword="null"/> when it is absent or blank.</summary>
    public static string? Normalize(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Stores <paramref name="note"/> and publishes it on <c>conversation.message</c>, answering
    /// <see cref="Written"/> once it is stored and <see cref="Failed"/> when it could not be. The stored message is the
    /// contract and the event a convenience, so a failed publish leaves the answer at <see cref="Written"/>.
    /// </summary>
    public static async Task<string> TryWriteAsync(
        HttpContext context,
        IMessageStore messages,
        IClock clock,
        PlatformEventBus eventBus,
        Conversation conversation,
        string note)
    {
        var userId = CallerIdentity.ResolveUserIdOrSystem(context.User);
        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            Direction = MessageDirection.System,
            Channel = conversation.Channel,
            SenderId = userId,
            Content = new MessageEnvelope([new TextBlock(note)]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = clock.UtcNow,
            CreatedBy = $"transfer:{userId}",
        };

        try
        {
            await messages.SaveAsync(message, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogNoteWriteFailed(LoggerOf(context), ex.GetType().Name, conversation.ConversationId.Value, conversation.TenantId.Value);
            return Failed;
        }

        try
        {
            eventBus.Publish(new ConversationMessageEvent(
                conversation.TenantId.Value, conversation.ConversationId.Value, message.MessageId.Value, "System", note));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogNotePublishFailed(LoggerOf(context), ex.GetType().Name, conversation.ConversationId.Value, conversation.TenantId.Value);
        }

        return Written;
    }

    private static ILogger LoggerOf(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

    [LoggerMessage(EventId = 7511, Level = LogLevel.Warning,
        Message = "The transfer note of conversation {ConversationId} in tenant {TenantId} could not be stored ({ExceptionType}); the transfer itself was made.")]
    private static partial void LogNoteWriteFailed(ILogger logger, string exceptionType, string conversationId, string tenantId);

    [LoggerMessage(EventId = 7512, Level = LogLevel.Warning,
        Message = "The transfer note of conversation {ConversationId} in tenant {TenantId} was stored but could not be published ({ExceptionType}).")]
    private static partial void LogNotePublishFailed(ILogger logger, string exceptionType, string conversationId, string tenantId);
}
