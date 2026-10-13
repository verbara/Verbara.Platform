using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>
/// The audit trail of a listen (design D10): <c>supervision.listen_started</c> when the originate is accepted, and
/// <c>supervision.listen_stopped</c> exactly once when it ends (the registry's single <c>TryEnd</c> guarantees that).
/// Both target the conversation (category <c>conversations</c>) and carry <c>listen_id</c>, <c>destination</c>,
/// <c>agent_id</c>, <c>supervisor_user_id</c> and, for a phone listen, only <c>phone_last4</c>. A request names the
/// caller as actor with its impersonation context; a natural end names the system. Best-effort: a failed write is
/// logged and never fails the listen.
/// <para>
/// Only an accepted listen is audited (<see cref="ActiveListen.Accepted"/>, decided under the registry's lock), so a
/// listen whose originate failed writes neither entry. The writes take no request token: they record a side effect
/// that already happened (an accepted originate, an ended registry entry), so a cancelled request never skips them.
/// </para>
/// </summary>
internal sealed partial class SupervisorListenAudit(IAuditService audit, ILogger<SupervisorListenAudit> logger)
{
    public const string Started = "supervision.listen_started";
    public const string Stopped = "supervision.listen_stopped";

    public Task StartedAsync(ActiveListen listen, ClaimsPrincipal caller) =>
        WriteAsync(Started, listen, Metadata(listen), caller);

    /// <summary>
    /// Writes <c>supervision.listen_stopped</c> for an ended listen, from the end the registry recorded
    /// (<see cref="SupervisorListenRegistry.TryEnd"/>): <c>end_reason</c> and <c>listened_seconds</c>. The actor is the
    /// stopping caller, or the system for a natural end.
    /// </summary>
    public Task StoppedAsync(ActiveListen listen)
    {
        var endedAt = listen.EndedAt ?? listen.StartedAt;
        var metadata = Metadata(listen);
        metadata["end_reason"] = listen.EndReason ?? ListenVocabulary.NotAnswered;
        metadata["listened_seconds"] = listen.ListenedSeconds(endedAt).ToString(CultureInfo.InvariantCulture);
        return WriteAsync(Stopped, listen, metadata, listen.EndedBy);
    }

    private static Dictionary<string, string> Metadata(ActiveListen listen)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["listen_id"] = listen.ListenId,
            ["destination"] = listen.Destination,
            ["agent_id"] = listen.AgentId,
            ["supervisor_user_id"] = listen.SupervisorUserId.Value,
        };
        if (listen.PhoneLast4 is { } last4)
            metadata["phone_last4"] = last4;
        return metadata;
    }

    private async Task WriteAsync(string action, ActiveListen listen, Dictionary<string, string> metadata, ClaimsPrincipal? caller)
    {
        string actorId;
        string actorType;
        if (caller is null)
        {
            actorId = "system";
            actorType = "system";
        }
        else
        {
            CallerIdentity.AddImpersonationContext(metadata, caller);
            actorId = CallerIdentity.ResolveUserIdOrSystem(caller);
            actorType = "user";
        }

        try
        {
            await audit.RecordAsync(
                listen.TenantId,
                category: "conversations",
                action: action,
                severity: "info",
                actorId: actorId,
                actorType: actorType,
                targetId: listen.ConversationId,
                targetType: "Conversation",
                metadata: metadata,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // No token of ours can cancel this write, so even an OperationCanceledException is the store's own failure
            // (a timeout): logged like any other, never thrown into the listen.
            LogAuditWriteFailed(ex, action, listen.ListenId, listen.TenantId.Value);
        }
    }

    [LoggerMessage(EventId = 7530, Level = LogLevel.Warning,
        Message = "The {Action} audit entry for listen {ListenId} in tenant {TenantId} could not be written; the listen itself is unaffected.")]
    private partial void LogAuditWriteFailed(Exception exception, string action, string listenId, string tenantId);
}
