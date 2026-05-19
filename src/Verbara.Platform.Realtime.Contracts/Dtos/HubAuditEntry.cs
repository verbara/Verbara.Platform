namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Audit-log entry payload posted from Verbara.Platform.Realtime to
/// <c>POST /api/v1/internal/hub-audit</c> on Verbara.Platform.Api when a
/// hub event needs persistent recording (cross-tenant subscription denials,
/// supervisor whisper attempts, etc.). Mirrors the Pro.Push.SignalR.Authz
/// HubAuditEntry shape so the Realtime sink adapter is a straight field copy.
/// Fire-and-forget — Realtime does not wait for ack.
/// </summary>
/// <param name="Action">Stable action token (e.g. <c>hub.cross_tenant_subscription_denied</c>).</param>
/// <param name="ActorTenantId">Tenant of the connected user (from JWT <c>tid</c> claim).</param>
/// <param name="ActorId">Subject identifier of the connected user (JWT <c>sub</c> claim, fallback <c>"unknown"</c>).</param>
/// <param name="TargetId">Identifier of the entity the actor tried to act on (typically the agent id).</param>
/// <param name="Metadata">Optional free-form serialised metadata.</param>
/// <param name="At">UTC timestamp Realtime observed the event.</param>
public sealed record HubAuditEntry(
    string Action,
    string ActorTenantId,
    string ActorId,
    string? TargetId,
    string? Metadata,
    DateTimeOffset At);
