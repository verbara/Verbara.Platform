using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The live-stream audience of a <c>conversation.message</c> a publisher writes without holding the conversation
/// (contact-history-opens-by-assignment, design D17 "Where each publisher gets its audience"): the coaching note, the
/// typing frame and the transfer note read the conversation by id in its tenant when they publish.
/// </summary>
/// <remarks>
/// A read that fails is contained (spec audit round 2, F3): the publisher has already stored what it publishes, or (the
/// transfer note) the move has already committed, so a failing read never fails or skips it. The message is published with
/// <see cref="ConversationAudience.None"/>, and reaches only the readers of every conversation, as a conversation that
/// cannot be found does. A cancellation of <c>ct</c> itself still propagates; the stored-write publishers pass
/// <see cref="CancellationToken.None"/>, so a client that hangs up changes nothing. The exception is logged by its type
/// only, since a database error can quote the row.
/// </remarks>
internal static partial class ConversationAudienceRead
{
    private const string LoggerCategory = "Verbara.Platform.Api.Endpoints.ConversationAudienceRead";

    /// <summary>The audience of <paramref name="conversationId"/> as it is now, or an empty one when it cannot be read.</summary>
    public static async Task<ConversationAudience> CurrentAsync(
        IConversationStore conversations, ILoggerFactory loggers, TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        try
        {
            return ConversationOffer.AudienceOf(await conversations.GetByIdAsync(tenantId, conversationId, ct));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogAudienceReadFailed(loggers.CreateLogger(LoggerCategory), ex.GetType().Name, conversationId.Value, tenantId.Value);
            return ConversationAudience.None;
        }
    }

    [LoggerMessage(EventId = 7513, Level = LogLevel.Warning,
        Message = "The live-stream audience of conversation {ConversationId} in tenant {TenantId} could not be read ({ExceptionType}); its message reaches only the readers of every conversation.")]
    private static partial void LogAudienceReadFailed(ILogger logger, string exceptionType, string conversationId, string tenantId);
}
