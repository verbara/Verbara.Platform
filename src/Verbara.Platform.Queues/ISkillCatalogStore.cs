using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

/// <summary>
/// The routing skill catalog of each tenant: the skill definitions it defines and the proficiency of each of its
/// agents in a skill. Every member takes the tenant first and reads or writes only that tenant's rows. Names are
/// matched without regard to ASCII case; an upsert or an assign replaces the stored name's case.
/// </summary>
/// <remarks>
/// Writes of one assignment key (tenant, agent, skill name without case) never interleave: assign, unassign and the
/// removals of a skill delete are serialised per key, so the values they return are the ones the write itself
/// replaced or deleted, never ones read before it.
/// </remarks>
public interface ISkillCatalogStore
{
    /// <summary>The tenant's skill definitions, ordered by name without case.</summary>
    Task<IReadOnlyList<SkillDefinition>> ListSkillsAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Creates the tenant's skill, or replaces the one of the same name without case; returns the stored row.</summary>
    Task<SkillDefinition> UpsertSkillAsync(TenantId tenantId, SkillDefinition skill, CancellationToken ct);

    /// <summary>
    /// Deletes the tenant's skill definition and, with it, the tenant's assignments of that name.
    /// <see cref="SkillRemovalOutcome.NotFound"/> when the tenant defines no skill of that name, with or without
    /// <paramref name="force"/>, touching no assignment; <see cref="SkillRemovalOutcome.InUse"/> when, without
    /// <paramref name="force"/>, at least one of the tenant's agents holds it, removing nothing.
    /// </summary>
    Task<SkillRemoval> RemoveSkillAsync(TenantId tenantId, string name, bool force, CancellationToken ct);

    /// <summary>The tenant's agents that hold the skill, ordered by agent id.</summary>
    Task<IReadOnlyList<AgentSkillProficiency>> ListAgentsWithSkillAsync(TenantId tenantId, string skillName, CancellationToken ct);

    /// <summary>The skills one of the tenant's agents holds, ordered by name without case.</summary>
    Task<IReadOnlyList<AgentSkillProficiency>> ListAgentSkillsAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);

    /// <summary>
    /// Assigns the skill to the tenant's agent, replacing the assignment of the same name without case. The result
    /// carries the stored row and the assignment it replaced (or none), both from the write itself;
    /// <see cref="SkillAssignOutcome.NoSuchAgent"/> when the tenant has no such agent (it may have just been deleted).
    /// </summary>
    Task<SkillAssignment> AssignAsync(TenantId tenantId, AgentSkillProficiency assignment, CancellationToken ct);

    /// <summary>Removes the agent's assignment of the skill; returns the row it deleted, or null when there was none.</summary>
    Task<AgentSkillProficiency?> UnassignAsync(TenantId tenantId, EntityId agentId, string skillName, CancellationToken ct);

    /// <summary>Every assignment of the tenant's agents, ordered by agent id, then by name without case.</summary>
    Task<IReadOnlyList<AgentSkillProficiency>> ListProficienciesAsync(TenantId tenantId, CancellationToken ct);
}

/// <summary>A skill the tenant defines.</summary>
public sealed record SkillDefinition(string Name, string? Category, string? Description);

/// <summary>One agent's proficiency in one skill.</summary>
public sealed record AgentSkillProficiency(EntityId AgentId, string SkillName, int Proficiency);

/// <summary>What <see cref="ISkillCatalogStore.AssignAsync"/> did.</summary>
public enum SkillAssignOutcome
{
    /// <summary>The assignment is stored.</summary>
    Assigned,

    /// <summary>The tenant has no such agent; nothing is stored.</summary>
    NoSuchAgent,
}

/// <summary>
/// The result of an assign: on <see cref="SkillAssignOutcome.Assigned"/>, the stored row and the assignment it
/// replaced (null for a first assign), both taken from the write.
/// </summary>
public sealed record SkillAssignment(SkillAssignOutcome Outcome, AgentSkillProficiency? Stored, AgentSkillProficiency? Replaced)
{
    /// <summary>The tenant has no such agent.</summary>
    public static SkillAssignment NoSuchAgent { get; } = new(SkillAssignOutcome.NoSuchAgent, null, null);

    /// <summary>The assignment is stored, replacing <paramref name="replaced"/> (null when there was none).</summary>
    public static SkillAssignment Assigned(AgentSkillProficiency stored, AgentSkillProficiency? replaced) =>
        new(SkillAssignOutcome.Assigned, stored, replaced);
}

/// <summary>What <see cref="ISkillCatalogStore.RemoveSkillAsync"/> did.</summary>
public enum SkillRemovalOutcome
{
    /// <summary>The tenant defines no skill of that name; nothing was removed.</summary>
    NotFound,

    /// <summary>Without force, some of the tenant's agents hold the skill; nothing was removed.</summary>
    InUse,

    /// <summary>The definition and the tenant's assignments of it were removed.</summary>
    Removed,
}

/// <summary>The result of a skill delete; <see cref="HolderCount"/> counts the tenant's holders on <see cref="SkillRemovalOutcome.InUse"/>.</summary>
public sealed record SkillRemoval(SkillRemovalOutcome Outcome, int HolderCount)
{
    public static SkillRemoval NotFound { get; } = new(SkillRemovalOutcome.NotFound, 0);
    public static SkillRemoval Removed { get; } = new(SkillRemovalOutcome.Removed, 0);
    public static SkillRemoval InUse(int holderCount) => new(SkillRemovalOutcome.InUse, holderCount);
}
