using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// The outcome of resolving a conversation against the published schema bindings:
/// the winning published <see cref="TypificationSchema"/> and an optional sub-tree
/// root node (<see langword="null"/> = the whole schema applies).
/// </summary>
public sealed record ResolvedTypification(TypificationSchema Schema, EntityId? SubtreeRoot);
