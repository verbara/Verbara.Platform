using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// ADR-0012 Ola-3 — shared best-effort realtime-sync deferral logger (EventId 4130).
/// The store decorators (<see cref="RealtimeSyncingQueueStore"/>,
/// <see cref="RealtimeSyncingAgentStore"/>, <see cref="RealtimeSyncingQueueMembershipStore"/>)
/// each sync to Asterisk Realtime on a BEST-EFFORT basis: a sync throw must never fail the
/// underlying store write, because the <see cref="RealtimeReconciliationService"/> re-converges
/// any missed upsert on its next pass. When a sync throws, the decorator swallows the exception
/// and logs it here at Warning so the eventual-consistency contract stays visible (never a silent
/// <c>catch {}</c>, which ADR-0012 gate #6 forbids).
/// </summary>
internal static partial class RealtimeSyncDeferralLog
{
    [LoggerMessage(EventId = 4130, Level = LogLevel.Warning,
        Message = "Best-effort Asterisk realtime sync deferred for {Operation} '{Entity}' (tenant {TenantId}); the realtime reconciler re-converges on its next pass.")]
    public static partial void Deferred(
        ILogger logger, string operation, string entity, string tenantId, Exception exception);
}
