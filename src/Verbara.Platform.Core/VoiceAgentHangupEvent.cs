namespace Verbara.Platform.Core;

/// <summary>
/// Raised when a voice call's agent leg ends (csat-completion, Platform/ADR-0020). Published from
/// <c>VoiceConversationBridge.OnCallEndedAsync</c> at the point the existing
/// <c>IsAbnormalAgentHangup</c> verdict is computed, so a voice-CSAT consumer can decide whether to
/// solicit while the caller leg is still up: a clean hangup (<see cref="Abnormal"/> <c>false</c>) is a
/// candidate to survey; an abnormal leg death (<see cref="Abnormal"/> <c>true</c>) should NOT strand
/// the dropped customer in a survey IVR.
/// </summary>
/// <remarks>
/// In-process only — carried on the <see cref="PlatformEventBus"/> (not serialized over the wire), so
/// it needs no <c>[JsonSerializable]</c> registration. It is published inside the bridge's
/// leader-gated, per-call-stripe-locked handler, so it inherits the exactly-once-cluster-wide
/// guarantee. Reflection-free (Native AOT, Platform/ADR-0022).
/// </remarks>
/// <param name="TenantId">The tenant the call belongs to.</param>
/// <param name="ConversationId">The tracked voice conversation id.</param>
/// <param name="QueueName">The originating queue name (empty when it could not be resolved).</param>
/// <param name="Abnormal">
/// The <c>IsAbnormalAgentHangup</c> verdict: <c>true</c> when the agent leg died abnormally (a
/// non-normal cause and the agent left first/together, or the caller was still present); <c>false</c>
/// for a clean hangup or when there is not enough evidence to claim abnormal (conservative default).
/// </param>
/// <param name="HangupAt">The instant the call ended.</param>
public sealed record VoiceAgentHangupEvent(
    string TenantId,
    string ConversationId,
    string QueueName,
    bool Abnormal,
    DateTimeOffset HangupAt)
    : PlatformEvent(TenantId, "voice.agent_hangup", HangupAt);
