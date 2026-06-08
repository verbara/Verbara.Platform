using Verbara.Platform.Conversations;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// Picks the published typification schema (and optional sub-tree root) that applies
/// to a given conversation by resolving its <see cref="SchemaBinding"/>s
/// most-specific-wins.
/// </summary>
public interface ITypificationResolver
{
    /// <summary>
    /// Resolves the binding that applies to <paramref name="conversation"/>, returning
    /// the winning published schema + optional sub-tree root, or <see langword="null"/>
    /// when no binding matches (or no matching binding has a published schema).
    /// </summary>
    Task<ResolvedTypification?> ResolveForConversationAsync(Conversation conversation, CancellationToken ct);
}
