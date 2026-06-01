using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Parses the platform agent id out of the Asterisk realtime member interface
/// <c>PJSIP/{tenant}-agent-{agentId}</c>. The SDK's <c>CallSession.AgentId</c> is the raw AMI
/// agent field (often EMPTY for app_queue), so the member interface is the reliable source.
/// Shared by <see cref="VoiceConversationBridge"/> and <see cref="AgentAssistBridge"/> so the
/// agent-targeted voice screen-pop AND the agent-assist events carry the SAME <c>Agent.AgentId</c>
/// the React client filters on (<c>isForCurrentAgent</c>); a mismatch silently drops every event.
/// </summary>
internal static class AgentInterfaceParser
{
    /// <summary>
    /// Returns the platform agent id embedded in <paramref name="agentInterface"/> for the given
    /// <paramref name="tenant"/>, or <see langword="null"/> when the interface is absent, not a
    /// tenant agent endpoint, or carries a blank id (a whitespace-only suffix would otherwise throw
    /// in <see cref="EntityId.From"/>).
    /// </summary>
    public static EntityId? ExtractAgentId(string tenant, string? agentInterface)
    {
        if (string.IsNullOrEmpty(agentInterface))
            return null;

        var prefix = $"PJSIP/{tenant}-agent-";
        if (!agentInterface.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var id = agentInterface[prefix.Length..];
        return string.IsNullOrWhiteSpace(id) ? null : EntityId.From(id);
    }
}
