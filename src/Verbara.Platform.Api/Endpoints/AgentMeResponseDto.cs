using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Response payload for <c>GET /agents/me</c>. Mirrors the serialized shape of
/// <see cref="Agent"/> (kept stable for the Web client) and — unlike the entity,
/// whose <c>SipPassword</c> is <c>[JsonIgnore]</c>d — deliberately surfaces the
/// caller's own <see cref="Extension"/> + <see cref="SipPassword"/> so the
/// in-browser SIP.js softphone can REGISTER (Phase 3A). This is the SINGLE
/// place the plaintext SIP secret crosses an HTTP boundary, and only ever for
/// the authenticated agent's own record (the endpoint resolves the agent from
/// the caller's user id — JWT <c>sub</c> / API-key <c>user_id</c>).
/// </summary>
internal sealed record AgentMeResponseDto(
    string AgentId,
    string TenantId,
    string UserId,
    string DisplayName,
    AgentState State,
    ChannelCapacity Capacity,
    string? TeamId,
    IReadOnlyList<string> Skills,
    string? Extension,
    string? SipPassword,
    bool? AutoAnswer,
    bool CanAcceptWork,
    // W4 — deferred ("pause-when-free") target. Non-null when the agent requested
    // an aux state that is held until active work drains. State stays the real
    // (still-working) state; these fields tell the client a pause is pending.
    AgentState? PendingState,
    string? PendingReason,
    DateTimeOffset? PendingSince,
    // W4 — count of conversations the agent actively owns (engaged states). The
    // client uses it to show "N items must finish before your pause applies".
    int ActiveWorkCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static AgentMeResponseDto FromAgent(Agent agent, int activeWorkCount)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new AgentMeResponseDto(
            AgentId: agent.AgentId.Value,
            TenantId: agent.TenantId.Value,
            UserId: agent.UserId.Value,
            DisplayName: agent.DisplayName,
            State: agent.State,
            // W6-A6 will resolve via IAgentCapacityResolver (tenant default)
            Capacity: agent.CapacityOverride.ToEffective(new ChannelCapacity()),
            TeamId: agent.TeamId?.Value,
            Skills: agent.Skills,
            Extension: agent.Extension,
            SipPassword: agent.SipPassword,
            AutoAnswer: agent.AutoAnswer,
            CanAcceptWork: agent.CanAcceptWork,
            PendingState: agent.PendingState,
            PendingReason: agent.PendingReason,
            PendingSince: agent.PendingSince,
            ActiveWorkCount: activeWorkCount,
            CreatedAt: agent.CreatedAt,
            UpdatedAt: agent.UpdatedAt);
    }
}
