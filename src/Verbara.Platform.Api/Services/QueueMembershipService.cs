using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Routing.Skills;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Computes effective queue members by merging skill-derived memberships,
/// manual overrides, and exclusions: (skill-derived ∪ manual) - excluded.
/// </summary>
internal sealed class QueueMembershipService
{
    private readonly IQueueStore _queueStore;
    private readonly IAgentStore _agentStore;
    private readonly SkillCatalogBase _skillCatalog;
    private readonly IQueueMembershipStore _membershipStore;
    private readonly ILogger<QueueMembershipService> _logger;

    public QueueMembershipService(
        IQueueStore queueStore,
        IAgentStore agentStore,
        SkillCatalogBase skillCatalog,
        IQueueMembershipStore membershipStore,
        ILogger<QueueMembershipService> logger)
    {
        _queueStore = queueStore;
        _agentStore = agentStore;
        _skillCatalog = skillCatalog;
        _membershipStore = membershipStore;
        _logger = logger;
    }

    /// <summary>
    /// Computes effective queue members for a tenant:
    /// (skill-derived ∪ manual overrides) - exclusions
    /// </summary>
    public async Task<IReadOnlyList<EffectiveQueueMember>> ComputeEffectiveMembersAsync(
        string tenantId, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);

        // 1. Load all queues and agents
        var queues = await _queueStore.ListAsync(tid, new PagedQuery(1, 1000), ct);
        var agents = await _agentStore.ListAsync(tid, new AgentQuery { PageSize = 1000 }, ct);

        // 2. Build skill-derived memberships
        var skillDerived = new List<EffectiveQueueMember>();
        foreach (var queue in queues.Items.Where(q => q.IsActive))
        {
            if (queue.RequiredSkills.Count == 0) continue;

            foreach (var agent in agents.Items)
            {
                if (string.IsNullOrEmpty(agent.Extension) || string.IsNullOrEmpty(agent.SipPassword))
                    continue; // Not provisioned — skip

                // Check skill match: agent must have at least one required skill
                var matchingSkills = queue.RequiredSkills
                    .Where(s => agent.Skills.Contains(s))
                    .ToList();
                if (matchingSkills.Count == 0) continue;

                // Compute penalty from proficiency
                var agentSkills = await _skillCatalog.GetAgentSkillsAsync(agent.AgentId.Value, ct);
                var avgProficiency = matchingSkills
                    .Select(s => agentSkills.FirstOrDefault(a => a.SkillName == s)?.Proficiency ?? 5)
                    .Average();
                var penalty = Math.Max(0, 10 - (int)Math.Round(avgProficiency));

                skillDerived.Add(new EffectiveQueueMember(
                    queue.QueueId.Value, queue.Name, agent.AgentId.Value,
                    agent.DisplayName, penalty, MembershipSource.Skill));
            }
        }

        // 3. Load manual overrides and exclusions from store
        var stored = await _membershipStore.ListByTenantAsync(tid, ct);
        var manualOverrides = stored.Where(m => m.Source == MembershipSource.Manual && !m.IsExcluded);
        var exclusions = stored.Where(m => m.IsExcluded)
            .Select(m => (m.QueueId.Value, m.AgentId.Value))
            .ToHashSet();

        // 4. Merge: (skill-derived ∪ manual) - excluded
        var result = new Dictionary<(string QueueId, string AgentId), EffectiveQueueMember>();

        // Add skill-derived (lower precedence)
        foreach (var m in skillDerived)
        {
            if (!exclusions.Contains((m.QueueId, m.AgentId)))
                result[(m.QueueId, m.AgentId)] = m;
        }

        // Add/override manual (higher precedence for penalty)
        foreach (var m in manualOverrides)
        {
            var key = (m.QueueId.Value, m.AgentId.Value);
            if (!exclusions.Contains(key))
            {
                // Need agent display name + queue name
                var agent = agents.Items.FirstOrDefault(a => a.AgentId == m.AgentId);
                var queue = queues.Items.FirstOrDefault(q => q.QueueId == m.QueueId);
                if (agent is not null && queue is not null &&
                    !string.IsNullOrEmpty(agent.Extension) && !string.IsNullOrEmpty(agent.SipPassword))
                {
                    result[key] = new EffectiveQueueMember(
                        m.QueueId.Value, queue.Name, m.AgentId.Value,
                        agent.DisplayName, m.Penalty, MembershipSource.Manual);
                }
            }
        }

        return result.Values.ToList();
    }
}

internal sealed record EffectiveQueueMember(
    string QueueId, string QueueName, string AgentId,
    string DisplayName, int Penalty, MembershipSource Source);
