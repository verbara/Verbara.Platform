namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Audit-log entry payload posted from Verbara.Platform.Realtime to
/// <c>POST /api/v1/internal/hub-audit</c> on Verbara.Platform.Api when a
/// hub event needs persistent recording (cross-tenant subscription denials,
/// supervisor whisper attempts, etc.). Fire-and-forget — Realtime does not
/// wait for ack.
/// </summary>
/// <param name="ActorId">The user identifier of the actor performing the action (from JWT <c>sub</c> claim).</param>
/// <param name="SubjectAgentId">The agent identifier the action targeted (may equal ActorId for self-actions).</param>
/// <param name="DeniedReason">Short machine-readable reason code (e.g. <c>"cross_tenant_denied"</c>, <c>"role_not_supervisor"</c>).</param>
/// <param name="At">UTC timestamp Realtime observed the event.</param>
public sealed record HubAuditEntry(
    string ActorId,
    string SubjectAgentId,
    string DeniedReason,
    DateTimeOffset At);
