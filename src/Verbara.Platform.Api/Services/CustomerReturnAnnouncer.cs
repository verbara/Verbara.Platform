using System.Runtime.ExceptionServices;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// conversations-close-after-inactivity (design D8, D13): announces a <c>Resolved → Closed</c> return close that an
/// inbound delivery won (<see cref="PipelineResult.ClosedOnReturnId"/>). The pipeline has no bus and no audit, so both
/// inbound callers (<see cref="WebhookInboundProcessor"/>, <see cref="WebChatInboundRouter"/>) hand the flag here before
/// they announce the new conversation, including on a duplicate result that carries it (the lost-insert branch).
/// </summary>
/// <remarks>
/// <para>
/// One announcement is one <see cref="ConversationStateChangedEvent"/> (<c>Resolved → Closed</c>), one increment of
/// <c>conversations.system_closed{reason=customer-returned}</c> and one <c>conversation.closed</c> audit entry with
/// the system actor <see cref="ActorId"/> and <c>reason = customer-returned</c>.
/// </para>
/// <para>
/// <b>It never throws</b> (audit round 4, P3). The close is committed and the message is stored; each part that fails
/// (a throwing event subscriber, the audit store) is logged and the others still run, so an announcement fault never
/// fails a delivery, never makes a provider retry, and never skips the routing of the new conversation.
/// </para>
/// </remarks>
internal sealed partial class CustomerReturnAnnouncer
{
    /// <summary>System actor of the <c>conversation.closed</c> audit entry of a return close (D8).</summary>
    internal const string ActorId = "verbara:system:customer-returned";

    private readonly PlatformEventBus _eventBus;
    private readonly IAuditService _auditService;
    private readonly ConversationCloseMetrics _metrics;
    private readonly ILogger<CustomerReturnAnnouncer> _logger;

    public CustomerReturnAnnouncer(
        PlatformEventBus eventBus,
        IAuditService auditService,
        ConversationCloseMetrics metrics,
        ILogger<CustomerReturnAnnouncer> logger)
    {
        _eventBus = eventBus;
        _auditService = auditService;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Announces the won return close of <paramref name="closedConversationId"/> once. Never throws.</summary>
    public async Task AnnounceAsync(TenantId tenantId, EntityId closedConversationId)
    {
        try
        {
            _eventBus.Publish(new ConversationStateChangedEvent(
                tenantId.Value, closedConversationId.Value,
                nameof(ConversationState.Resolved), nameof(ConversationState.Closed)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishFailed(ex, tenantId.Value, closedConversationId.Value);
        }

        try
        {
            _metrics.RecordClosed(ConversationCloseReasons.CustomerReturned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCountFailed(ex, tenantId.Value, closedConversationId.Value);
        }

        try
        {
            // The close is committed: written with CancellationToken.None, as the inactivity close's audit (D6).
            await _auditService.RecordAsync(
                tenantId,
                category: "conversations",
                action: "conversation.closed",
                severity: "info",
                actorId: ActorId,
                actorType: "system",
                targetId: closedConversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = ConversationCloseReasons.CustomerReturned,
                    ["previous_state"] = nameof(ConversationState.Resolved),
                },
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailed(ex, tenantId.Value, closedConversationId.Value);
        }
    }

    /// <summary>
    /// Runs <paramref name="pipeline"/> for one inbound message. When it fails after a won return close
    /// (<see cref="InboundReturnClosedException"/>, audit round 4 F1), announces that close and rethrows the original
    /// failure unchanged, so the delivery fails exactly as before (a provider retries; a retry finds the row closed and
    /// carries no flag, so the close is announced once).
    /// </summary>
    public async Task<PipelineResult> ProcessAsync(
        IInboundMessagePipeline pipeline, InboundMessage inbound, TenantId tenantId, ChannelType channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        try
        {
            return await pipeline.ProcessAsync(inbound, tenantId, channel, ct).ConfigureAwait(false);
        }
        catch (InboundReturnClosedException ex) when (ex.InnerException is not null)
        {
            await AnnounceAsync(ex.TenantId, ex.ClosedOnReturnId).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }

    [LoggerMessage(EventId = 7420, Level = LogLevel.Error,
        Message = "Return close of conversation {ConversationId} (tenant {TenantId}) committed but its state-changed event could not be published")]
    private partial void LogPublishFailed(Exception ex, string tenantId, string conversationId);

    [LoggerMessage(EventId = 7421, Level = LogLevel.Warning,
        Message = "Return close of conversation {ConversationId} (tenant {TenantId}) committed but it could not be counted")]
    private partial void LogCountFailed(Exception ex, string tenantId, string conversationId);

    [LoggerMessage(EventId = 7422, Level = LogLevel.Warning,
        Message = "Return close of conversation {ConversationId} (tenant {TenantId}) committed but its audit entry could not be written")]
    private partial void LogAuditFailed(Exception ex, string tenantId, string conversationId);
}
