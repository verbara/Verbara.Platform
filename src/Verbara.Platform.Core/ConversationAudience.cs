namespace Verbara.Platform.Core;

/// <summary>
/// The agents a content-bearing event is for, besides the readers of every conversation (contact-history-opens-by-assignment,
/// design D17; Platform/ADR-0047). The live stream delivers an <see cref="IConversationContentEvent"/> to a subscriber
/// that reads every conversation, or to a handle holder whose Agent profile is <see cref="OwnerAgentId"/> or
/// <see cref="OfferedToAgentId"/>, and to nobody else.
/// </summary>
/// <remarks>
/// The audience is fixed per event type and the types do not mix: <c>conversation.message</c> carries the conversation's
/// owner (kind <c>Agent</c>) and the agent it is open-offered to, read when the event is published; the four
/// <c>agentassist.*</c> carry only the agent the event names in <c>AgentId</c>, in <see cref="OwnerAgentId"/>;
/// <c>csat.response_recorded</c> carries <see cref="None"/>. A publisher that cannot name its audience stamps
/// <see cref="None"/>, and the event then reaches only the readers of every conversation (fail closed).
/// </remarks>
/// <param name="OwnerAgentId">The owner agent's id, or the agent an <c>agentassist.*</c> event names; <see langword="null"/> when none.</param>
/// <param name="OfferedToAgentId">The agent the conversation is open-offered to; <see langword="null"/> when none.</param>
public sealed record ConversationAudience(string? OwnerAgentId, string? OfferedToAgentId)
{
    /// <summary>No agent: the event reaches only the readers of every conversation.</summary>
    public static ConversationAudience None { get; } = new(null, null);

    /// <summary>The one agent <paramref name="agentId"/>, or <see cref="None"/> when it is null or empty.</summary>
    public static ConversationAudience ForAgent(string? agentId) =>
        string.IsNullOrEmpty(agentId) ? None : new ConversationAudience(agentId, null);

    /// <summary>Whether <paramref name="agentId"/> is in the audience. Allocates nothing.</summary>
    public bool Includes(string agentId) =>
        string.Equals(OwnerAgentId, agentId, StringComparison.Ordinal)
        || string.Equals(OfferedToAgentId, agentId, StringComparison.Ordinal);
}

/// <summary>
/// A <see cref="PlatformEvent"/> that carries what a customer or an agent said or wrote (contact-history-opens-by-assignment,
/// design D17): <c>conversation.message</c>, the four <c>agentassist.*</c> and <c>csat.response_recorded</c>. The live
/// stream delivers it only to the readers of every conversation and to the agents of its <see cref="Audience"/>.
/// </summary>
/// <remarks>
/// <see cref="Audience"/> is never serialized (<c>[JsonIgnore]</c> on every implementer), so the SSE wire shape is
/// unchanged and no subscriber learns who owns a conversation from it. None of these events is cross-pod
/// (<see cref="ICrossPodEvent"/>): one that became so would arrive on another replica with <see cref="ConversationAudience.None"/>
/// and reach only the readers of every conversation, so a guard test fails until the backplane payload carries the audience.
/// </remarks>
public interface IConversationContentEvent
{
    /// <summary>The agents the event is for, besides the readers of every conversation.</summary>
    ConversationAudience Audience { get; }
}
