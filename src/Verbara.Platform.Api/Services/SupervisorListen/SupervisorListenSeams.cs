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

/// <summary><see cref="IVoiceCallLegs"/> over the SDK's <see cref="ICallSessionManager"/>.</summary>
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
