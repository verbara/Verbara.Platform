using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The event store's <see cref="ICallAgentResolver"/> (call-records-name-the-queue-agent design D1): names the Platform
/// agent of an answered queue call from the member interface the Sdk recorded, <c>PJSIP/{tenant}-agent-{agentId}</c>,
/// with the same <see cref="AgentInterfaceParser"/> that feeds the voice screen-pop, Agent Assist and the conversation's
/// owner, so the call record names the agent those name.
/// </summary>
/// <remarks>
/// It answers <see langword="null"/>, which keeps the agent id the Sdk recorded, for an interface outside Platform's
/// scheme (a <c>Local/…</c> member), of another tenant, of a supervisor's listen slot
/// (<see cref="AgentInterfaceParser.ListenSlotPrefix"/>) or with a blank id. A pure in-memory parse with no I/O, as the
/// seam asks, so it does not observe the token. Pro records the interface of the first member that took the call, so a
/// call answered in two queue visits names its first agent (Pro's first-member limitation, not decided here).
/// </remarks>
internal sealed class AgentInterfaceCallAgentResolver : ICallAgentResolver
{
    /// <inheritdoc />
    public ValueTask<string?> ResolveAgentIdAsync(
        string tenantId, string agentInterface, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AgentInterfaceParser.ExtractAgentId(tenantId, agentInterface)?.Value);
}
