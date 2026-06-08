using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// The outcome of resolving a conversation's captured context (its
/// <c>reasonPath</c> metadata and metadata-sourced fields) against a published
/// <see cref="TypificationSchema"/> for wrap-up prefill.
/// </summary>
/// <param name="PrefilledNodePath">
/// The validated node ids (root→leaf order) preselecting the cascade, or empty when
/// nothing is preselectable. May be a partial prefix of the captured path.
/// </param>
/// <param name="PrefilledFieldValues">
/// Field values keyed by <see cref="TypificationField.Key"/> (the Web form's
/// fieldValues keying), sourced from <see cref="PrefillSourceKind.Metadata"/> fields.
/// Empty when none apply.
/// </param>
public sealed record PrefillResult(
    IReadOnlyList<EntityId> PrefilledNodePath,
    IReadOnlyDictionary<string, string> PrefilledFieldValues);
