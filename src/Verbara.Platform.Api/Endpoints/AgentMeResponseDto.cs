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
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static AgentMeResponseDto FromAgent(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new AgentMeResponseDto(
            AgentId: agent.AgentId.Value,
            TenantId: agent.TenantId.Value,
            UserId: agent.UserId.Value,
            DisplayName: agent.DisplayName,
            State: agent.State,
            Capacity: agent.Capacity,
            TeamId: agent.TeamId?.Value,
            Skills: agent.Skills,
            Extension: agent.Extension,
            SipPassword: agent.SipPassword,
            AutoAnswer: agent.AutoAnswer,
            CanAcceptWork: agent.CanAcceptWork,
            CreatedAt: agent.CreatedAt,
            UpdatedAt: agent.UpdatedAt);
    }
}
