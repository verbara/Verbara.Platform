using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// In-memory <see cref="ISkillCatalogStore"/> for the host without PostgreSQL and the Api test hosts
/// (routing-skills-stay-in-their-tenant, design D9), with the semantics of <c>PostgresSkillCatalogStore</c>: definitions keyed by (tenant, name without case),
/// assignments by (tenant, agent, skill name without case), an upsert or assign replacing the stored spelling. Assign,
/// unassign and remove-skill are each one atomic step under one lock, returning the replaced and the deleted row from
/// that step, so no other write interleaves with them. An assign to an agent the tenant does not have reports
/// <see cref="SkillAssignOutcome.NoSuchAgent"/>, as the foreign key does on PostgreSQL. The agent-delete cascade is
/// not modelled: the in-memory agent store has no delete hook, and the cascade is proven on PostgreSQL.
/// </summary>
internal sealed class InMemorySkillCatalogStore(IAgentStore agents) : ISkillCatalogStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string TenantId, string Name), SkillDefinition> _skills = [];
    private readonly Dictionary<(string TenantId, string AgentId, string SkillName), AgentSkillProficiency> _assignments = [];

    public Task<IReadOnlyList<SkillDefinition>> ListSkillsAsync(TenantId tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<SkillDefinition> result = [.. _skills
                .Where(kv => kv.Key.TenantId == tenantId.Value)
                .Select(kv => kv.Value)
                .OrderBy(s => Fold(s.Name), StringComparer.Ordinal)
                .ThenBy(s => s.Name, StringComparer.Ordinal)];
            return Task.FromResult(result);
        }
    }

    public Task<SkillDefinition> UpsertSkillAsync(TenantId tenantId, SkillDefinition skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);
        lock (_gate)
        {
            _skills[(tenantId.Value, Fold(skill.Name))] = skill;
            return Task.FromResult(skill);
        }
    }

    public Task<SkillRemoval> RemoveSkillAsync(TenantId tenantId, string name, bool force, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = (tenantId.Value, Fold(name));
            if (!_skills.ContainsKey(key))
                return Task.FromResult(SkillRemoval.NotFound);

            var holders = _assignments.Keys
                .Where(k => k.TenantId == tenantId.Value && k.SkillName == key.Item2)
                .ToList();
            if (!force && holders.Count > 0)
                return Task.FromResult(SkillRemoval.InUse(holders.Count));

            foreach (var holder in holders)
                _assignments.Remove(holder);
            _skills.Remove(key);
            return Task.FromResult(SkillRemoval.Removed);
        }
    }

    public Task<IReadOnlyList<AgentSkillProficiency>> ListAgentsWithSkillAsync(
        TenantId tenantId, string skillName, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentSkillProficiency> result = [.. _assignments
                .Where(kv => kv.Key.TenantId == tenantId.Value && kv.Key.SkillName == Fold(skillName))
                .Select(kv => kv.Value)
                .OrderBy(a => a.AgentId.Value, StringComparer.Ordinal)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<AgentSkillProficiency>> ListAgentSkillsAsync(
        TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentSkillProficiency> result = [.. _assignments
                .Where(kv => kv.Key.TenantId == tenantId.Value && kv.Key.AgentId == agentId.Value)
                .Select(kv => kv.Value)
                .OrderBy(a => Fold(a.SkillName), StringComparer.Ordinal)
                .ThenBy(a => a.SkillName, StringComparer.Ordinal)];
            return Task.FromResult(result);
        }
    }

    public async Task<SkillAssignment> AssignAsync(TenantId tenantId, AgentSkillProficiency assignment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (await agents.GetByIdAsync(tenantId, assignment.AgentId, ct).ConfigureAwait(false) is null)
            return SkillAssignment.NoSuchAgent;

        lock (_gate)
        {
            var key = (tenantId.Value, assignment.AgentId.Value, Fold(assignment.SkillName));
            _assignments.TryGetValue(key, out var replaced);
            _assignments[key] = assignment;
            return SkillAssignment.Assigned(assignment, replaced);
        }
    }

    public Task<AgentSkillProficiency?> UnassignAsync(TenantId tenantId, EntityId agentId, string skillName, CancellationToken ct)
    {
        lock (_gate)
        {
            _assignments.Remove((tenantId.Value, agentId.Value, Fold(skillName)), out var deleted);
            return Task.FromResult(deleted);
        }
    }

    public Task<IReadOnlyList<AgentSkillProficiency>> ListProficienciesAsync(TenantId tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentSkillProficiency> result = [.. _assignments
                .Where(kv => kv.Key.TenantId == tenantId.Value)
                .Select(kv => kv.Value)
                .OrderBy(a => a.AgentId.Value, StringComparer.Ordinal)
                .ThenBy(a => Fold(a.SkillName), StringComparer.Ordinal)
                .ThenBy(a => a.SkillName, StringComparer.Ordinal)];
            return Task.FromResult(result);
        }
    }

    /// <summary>The case-insensitive key, as PostgreSQL's <c>lower()</c> folds an ASCII name.</summary>
    private static string Fold(string name) => name.ToLowerInvariant();
}
