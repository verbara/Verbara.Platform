using Verbara.Platform.Conversations;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The event store's tenant resolver by LinkedId (call-records-are-written-once design D5). A replica that was the
/// standby holds every call it observed without a tenant, because only the voice leader stamps one; when it becomes the
/// event store's writer, Pro asks this resolver once per call it retains tenant-less, live or ended inside the recovery
/// window. The order is: the Conversation the previous leader created for the LinkedId (the cross-tenant lookup the
/// bridge's hangup path uses); else, for a call born in the lease gap that is still up, its trunk leg's <c>TENANT_ID</c>
/// through the bridge's fail-closed read, the session found by LinkedId through the session manager (round 12 F1).
/// With neither the answer is no tenant: the event store drops the call past its window, counted, and logs its LinkedId.
/// A store that throws propagates, so the event store retries the call inside the window.
/// </summary>
internal sealed partial class VoiceCallTenantResolver(
    IConversationStore conversations,
    ICallSessionManager sessions,
    VerbaraServerPool serverPool,
    ILogger<VoiceCallTenantResolver> logger) : ICallTenantResolver
{
    /// <inheritdoc />
    public async ValueTask<string?> ResolveTenantIdAsync(string linkedId, CancellationToken cancellationToken = default)
    {
        var conversation = await conversations
            .FindByVoiceLinkedIdAcrossTenantsAsync(linkedId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is not null)
            return conversation.TenantId.Value;

        if (sessions.GetByLinkedId(linkedId) is not { } session || !VoiceCallTenant.IsLive(session))
        {
            LogNoSource(linkedId);
            return null;
        }

        var tenantId = await VoiceCallTenant.ResolveAsync(serverPool, session, logger).ConfigureAwait(false);
        if (tenantId is null)
            LogTrunkLegUnresolved(linkedId);
        return tenantId;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] LinkedId {LinkedId}: no Conversation and no live call session — the event store gets no tenant for the call.")]
    private partial void LogNoSource(string linkedId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] LinkedId {LinkedId}: no Conversation and no TENANT_ID on the live call's trunk leg — the event store gets no tenant for the call.")]
    private partial void LogTrunkLegUnresolved(string linkedId);
}
