using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// Resolves the wrap-up prefill for a conversation against a published schema: the
/// captured <c>reasonPath</c> metadata (a JSON array of node Codes) is validated
/// into a node-id path (longest valid prefix, parent-child chain enforced, and
/// constrained to the binding's sub-tree when one applies), and metadata-sourced
/// fields are projected into prefilled values keyed by field Key.
/// </summary>
/// <remarks>
/// Pure and synchronous — no I/O, no store access. Never throws on malformed
/// metadata; a bad <c>reasonPath</c> degrades to an empty node path.
/// </remarks>
public interface ITypificationPrefillResolver
{
    /// <summary>
    /// Resolves the prefill for <paramref name="conversation"/> against
    /// <paramref name="schema"/>. When <paramref name="subtreeRoot"/> is non-null the
    /// resolved node path must pass through it, otherwise the node path is empty (the
    /// captured reason is outside this binding's branch).
    /// </summary>
    PrefillResult ResolvePrefill(TypificationSchema schema, EntityId? subtreeRoot, Conversation conversation);
}
