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

    /// <summary>
    /// Optional per-binding override of the schema's <see cref="TypificationAiConfig"/>
    /// (E1). <see langword="null"/> = inherit the schema's AiConfig; a non-null value lets
    /// a single scope (e.g. one queue/campaign) pilot a different AI automation band
    /// without changing the schema default. The effective config is
    /// <c>AiConfigOverride ?? schema.AiConfig</c>.
    /// </summary>
    public TypificationAiConfig? AiConfigOverride { get; init; }

    /// <summary>
    /// Creation instant; tiebreak for equal-priority same-scope resolution
    /// (most-recent config wins).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
