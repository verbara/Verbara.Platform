using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// Maps a routing/entry context (the DID a call arrived on, the channel, or the
/// queue) to a captured reason taxonomy path, so the disposition form can be
/// prefilled with the most likely contact reason. Ties broken by <see cref="Priority"/>.
/// </summary>
public sealed record ReasonHint : ITenantScoped
{
    public required EntityId HintId { get; init; }
    public required TenantId TenantId { get; init; }
    public required ReasonHintScope Scope { get; init; }

    /// <summary>The DID in E.164 | ChannelType name | queueId string.</summary>
    public required string ScopeRef { get; init; }

    /// <summary>JSON array of node Codes, e.g. <c>["CITAS","REPROG","GINE"]</c>.</summary>
    public required string ReasonPath { get; init; }

    public int Priority { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Creation instant; tiebreak for equal-priority same-scope resolution
    /// (most-recent config wins).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
