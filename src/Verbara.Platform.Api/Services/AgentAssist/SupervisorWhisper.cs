using System.Globalization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// A supervisor's text whisper, delivered as an <c>agentassist.suggestion</c> (supervisor-whisper-reaches-the-agent,
/// design D1, D3): source <see cref="Source"/>, priority <see cref="Priority"/>, addressed to the session's bound agent.
/// </summary>
internal static class SupervisorWhisper
{
    /// <summary>The suggestion source of a whisper.</summary>
    public const string Source = "Supervisor";

    /// <summary>The suggestion priority of a whisper.</summary>
    public const string Priority = "Critical";

    /// <summary>The longest whisper text, in UTF-16 code units after trimming (design D7).</summary>
    public const int MaxTextLength = 500;

    /// <summary>
    /// The whisper's event for the session of <paramref name="record"/>, whose bound agent must be set: tenant, session
    /// and agent come from the record, never from the request. Constructed with the bound agent, never patched with
    /// <c>with</c>, so its <see cref="AgentAssistSuggestionEvent.Audience"/> is that agent.
    /// </summary>
    public static AgentAssistSuggestionEvent BuildEvent(
        LiveSessionRecord record, string conversationId, string text, string suggestionId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.AgentId);

        return new AgentAssistSuggestionEvent(
            record.OwnerTenant,
            record.SessionId,
            record.AgentId,
            conversationId,
            suggestionId,
            text,
            Priority,
            Source,
            TriggerPhrase: null);
    }

    /// <summary>A new suggestion id for one whisper: <c>sw-</c> and a new GUID in <c>N</c> format.</summary>
    public static string NewSuggestionId() => "sw-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
