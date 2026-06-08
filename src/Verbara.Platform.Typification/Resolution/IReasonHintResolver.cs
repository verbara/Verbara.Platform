using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// Resolves the most-specific <see cref="ReasonHint"/> for an inbound routing/entry
/// context (the DID a call arrived on, the owning queue, or the channel), so the
/// disposition form can be prefilled with the most likely contact reason. Returns
/// <see langword="null"/> when no active hint matches any scope.
/// </summary>
public interface IReasonHintResolver
{
    /// <summary>
    /// Resolves the most-specific active reason hint for the given inbound context,
    /// most-specific-wins in the order DID → Queue → Channel (a non-matching
    /// more-specific scope falls through to the next), or <see langword="null"/>
    /// when none matches.
    /// </summary>
    Task<ReasonHint?> ResolveAsync(TenantId tenantId, string? did, EntityId? queueId, ChannelType channel, CancellationToken ct);
}
