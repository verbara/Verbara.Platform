using Microsoft.Extensions.Logging;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Agent creation and deletion as an attributable trail (licensed-agent-metering, agent-identity-integrity):
/// every creation writes <c>agent.created</c> and every deletion — from the admin endpoint or a GDPR purge —
/// removes the agent's queue memberships, then the agent, and writes <c>agent.deleted</c>. Each entry
/// carries the actor, the agent id and the owning user id (ids only).
/// </summary>
/// <remarks>
/// The audit write is best-effort, as the other agent-lifecycle entries already are: the change has been
/// made when the entry is written, so a failed write is logged (EventId 7505) rather than turned into a
/// failed request.
/// </remarks>
internal static partial class AgentLifecycle
{
    public const string CreatedAction = "agent.created";
    public const string DeletedAction = "agent.deleted";

    /// <summary>Removes the agent's queue memberships, then the agent row.</summary>
    public static async Task DeleteAsync(
        IAgentStore agents, IQueueMembershipStore memberships, TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(memberships);

        // ADR-0012 Ola-3 — RemoveAgentAsync rides IAgentStore.DeleteAsync (RealtimeSyncingAgentStore).
        await memberships.DeleteAllForAgentAsync(tenantId, agentId, ct).ConfigureAwait(false);
        await agents.DeleteAsync(tenantId, agentId, ct).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="action"/> for <paramref name="agent"/>; logs instead of throwing.</summary>
    public static async Task TryAuditAsync(
        IAuditService audit,
        ILogger logger,
        string action,
        Agent agent,
        string actorId,
        string source,
        IReadOnlyDictionary<string, string>? extraMetadata,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(agent);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent_id"] = agent.AgentId.Value,
            ["user_id"] = agent.UserId.Value,
            ["source"] = source,
        };
        if (extraMetadata is not null)
        {
            foreach (var (key, value) in extraMetadata)
                metadata[key] = value;
        }

        try
        {
            await audit.RecordAsync(
                agent.TenantId,
                category: "queues",
                action: action,
                severity: action == DeletedAction ? "warning" : "info",
                actorId: actorId,
                actorType: "user",
                targetId: agent.AgentId.Value,
                targetType: "Agent",
                metadata: metadata,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditWriteFailed(logger, ex, action, agent.AgentId.Value, agent.TenantId.Value);
        }
    }

    [LoggerMessage(EventId = 7505, Level = LogLevel.Warning,
        Message = "The {Action} audit entry for agent {AgentId} in tenant {TenantId} could not be written; the change itself was made.")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception exception, string action, string agentId, string tenantId);
}
