using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// One live Agent Assist session as the supervisor routes read it: its id, the one tenant that owns it (or
/// <see langword="null"/> when none can be told) and its call. Internal, never serialized.
/// </summary>
internal sealed record LiveAssistSession(string SessionId, string? OwnerTenant, CallSession? Call);

/// <summary>
/// The live Agent Assist sessions of this process, seen through Platform. The Pro registry
/// (<see cref="AgentAssistSupervisor"/>) cannot be filled from a Platform test (its <c>Add</c> and the session
/// constructor are internal to Pro), so the routes read it through this seam.
/// </summary>
internal interface IAgentAssistLiveSessions
{
    /// <summary>Every live session, each with its owner tenant.</summary>
    IReadOnlyList<LiveAssistSession> Snapshot();

    /// <summary>
    /// Queues a supervisor whisper for <paramref name="sessionId"/>. <see langword="false"/> when the id names no live
    /// session, or the session does not deliver whispers.
    /// </summary>
    bool TryWhisper(string sessionId, string text);
}

/// <summary>
/// The production <see cref="IAgentAssistLiveSessions"/>: a view over the optional Pro <see cref="AgentAssistSupervisor"/>.
/// Without it (a deployment with no Pro Agent Assist) the snapshot is empty and every id is unknown.
/// </summary>
internal sealed class AgentAssistLiveSessions : IAgentAssistLiveSessions
{
    private readonly AgentAssistSupervisor? _supervisor;

    public AgentAssistLiveSessions(AgentAssistSupervisor? supervisor) => _supervisor = supervisor;

    /// <inheritdoc />
    public IReadOnlyList<LiveAssistSession> Snapshot()
    {
        if (_supervisor is null)
            return [];

        return [.. _supervisor.ActiveSessions.Values.Select(s => ToLive(s.SessionId, s.CallSession, s.TenantId))];
    }

    /// <inheritdoc />
    public bool TryWhisper(string sessionId, string text) =>
        _supervisor is not null
        && _supervisor.ActiveSessions.TryGetValue(sessionId, out var session)
        && session.TryEnqueueSupervisorWhisper(text);

    /// <summary>
    /// Maps one session: the call's tenant is read when this is called (it can be stamped after the session started),
    /// and the owner is <see cref="OwnerTenantOf"/> the call's tenant and the session's start-time
    /// <paramref name="sessionTenant"/>.
    /// </summary>
    internal static LiveAssistSession ToLive(string sessionId, CallSession? call, string? sessionTenant) =>
        new(sessionId, OwnerTenantOf(call?.TenantId, sessionTenant), call);

    /// <summary>
    /// The one tenant a live session's call and its start-time snapshot agree on: the call's when only it is set, the
    /// snapshot's when only it is set, that tenant when both are set and equal (ordinal), and <see langword="null"/>
    /// when both are set and differ or neither is set. Null, empty and whitespace count as not set.
    /// </summary>
    internal static string? OwnerTenantOf(string? callTenant, string? sessionTenant)
    {
        var call = string.IsNullOrWhiteSpace(callTenant) ? null : callTenant;
        var snapshot = string.IsNullOrWhiteSpace(sessionTenant) ? null : sessionTenant;

        if (call is null)
            return snapshot;
        if (snapshot is null)
            return call;
        return string.Equals(call, snapshot, StringComparison.Ordinal) ? call : null;
    }
}
