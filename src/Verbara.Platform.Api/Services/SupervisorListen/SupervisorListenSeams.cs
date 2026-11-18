using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>
/// The live legs of a voice call by its linked id (design D4). A seam because <c>CallSession.AddParticipant</c> is
/// SDK-internal, so tests cannot populate a real session.
/// </summary>
internal interface IVoiceCallLegs
{
    IReadOnlyList<SessionParticipant>? GetParticipants(string linkedId);
}

/// <summary>
/// <see cref="IVoiceCallLegs"/> over the SDK's <see cref="ICallSessionManager"/>. The unscoped linked-id lookup is
/// kept on purpose: a listen request carries a linked id only, with no server id. From Sdk 2.10 an id two or more
/// attached servers hold answers <see langword="null"/>, so the listen finds no agent leg and originates no listen leg
/// (fails closed) instead of joining whichever server's call last wrote a shared index. The chart gives each pod's
/// Asterisk its own <c>systemname</c>, which keeps linked ids unique across servers.
/// </summary>
internal sealed class CallSessionLegs(ICallSessionManager sessions) : IVoiceCallLegs
{
    public IReadOnlyList<SessionParticipant>? GetParticipants(string linkedId) =>
        sessions.GetByLinkedId(linkedId)?.Participants;
}

/// <summary>
/// Controls a listen's own leg (design D6): starts the AMI lifetime observer on first use, and hangs the leg up on a
/// stop — at once when its channel name is known, otherwise as soon as it appears. The agent's call is never touched.
/// </summary>
internal interface ISupervisorListenLegControl
{
    void EnsureObserving();

    Task HangupAsync(ActiveListen listen, CancellationToken ct);
}
