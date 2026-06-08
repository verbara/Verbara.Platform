using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// Binds a published schema (optionally a sub-branch) to a scope. Resolution is
/// most-specific-wins (Queue+Channel → Queue → Campaign → Channel → Direction →
/// Tenant default); ties broken by <see cref="Priority"/>.
/// </summary>
public sealed record SchemaBinding : ITenantScoped
{
    public required EntityId BindingId { get; init; }
    public required TenantId TenantId { get; init; }
    public required BindingScope Scope { get; init; }

    /// <summary>
    /// queueId | campaignId | ChannelType | "inbound"/"outbound"; null for Tenant.
    /// </summary>
    public string? ScopeRef { get; init; }

    public required EntityId SchemaId { get; init; }

    /// <summary>Optional: bind only a sub-branch of the schema.</summary>
    public EntityId? SubTreeRootNodeId { get; init; }

    public int Priority { get; init; }
}
