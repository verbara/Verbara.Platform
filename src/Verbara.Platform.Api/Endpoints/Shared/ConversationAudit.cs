using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Audit entries for changes made to a conversation through the API: who moved, took over, closed or
/// typified it, recorded against the conversation (category <c>conversations</c>, actor = the calling
/// user, with the impersonation context when the caller is impersonating).
/// </summary>
/// <remarks>
/// Best-effort, as the reassign entry already is: the change has been made when the entry is written,
/// so a failed write is logged rather than turned into a failed request.
/// </remarks>
internal static partial class ConversationAudit
{
    private const string LoggerCategory = "Verbara.Platform.Api.Endpoints.ConversationAudit";

    /// <summary>The metadata key naming the supervisor who acted on a conversation it does not own.</summary>
    public const string BySupervisor = "by_supervisor";

    public static async Task TryRecordAsync(
        HttpContext context,
        IAuditService audit,
        TenantId tenantId,
        string action,
        EntityId conversationId,
        Dictionary<string, string> metadata,
        CancellationToken ct)
    {
        CallerIdentity.AddImpersonationContext(metadata, context.User);
        try
        {
            await audit.RecordAsync(
                tenantId,
                category: "conversations",
                action: action,
                severity: "info",
                actorId: CallerIdentity.ResolveUserIdOrSystem(context.User),
                actorType: "user",
                targetId: conversationId.Value,
                targetType: "Conversation",
                metadata: metadata,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
            LogAuditWriteFailed(logger, ex, action, conversationId.Value, tenantId.Value);
        }
    }

    [LoggerMessage(EventId = 7510, Level = LogLevel.Warning,
        Message = "The {Action} audit entry for conversation {ConversationId} in tenant {TenantId} could not be written; the change itself was made.")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception exception, string action, string conversationId, string tenantId);
}
