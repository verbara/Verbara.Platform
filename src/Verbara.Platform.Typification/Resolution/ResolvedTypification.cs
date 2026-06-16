using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// The outcome of resolving a conversation against the published schema bindings:
/// the winning published <see cref="TypificationSchema"/>, an optional sub-tree root
/// node (<see langword="null"/> = the whole schema applies), and the
/// <paramref name="EffectiveAiConfig"/> — the AI automation config that actually governs
/// this conversation (E1): the winning binding's <see cref="SchemaBinding.AiConfigOverride"/>
/// when present, otherwise the schema's own <see cref="TypificationSchema.AiConfig"/>.
/// All suggestion-surfacing / write-path-screening decisions read this, not
/// <c>Schema.AiConfig</c>, so a single binding can pilot a different band.
/// </summary>
public sealed record ResolvedTypification(
    TypificationSchema Schema,
    EntityId? SubtreeRoot,
    TypificationAiConfig EffectiveAiConfig);
