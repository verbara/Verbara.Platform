using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// The validated result of a direct LLM classification of a conversation transcript
/// against a typification taxonomy (P2a — auto-disposition suggestion).
/// </summary>
/// <param name="NodePath">
/// The root→leaf <see cref="EntityId"/> chain of the suggested leaf node, walked and
/// validated against the schema (same shape the prefill resolver produces). Never empty
/// for a non-null classification — the leaf and every ancestor are guaranteed to exist.
/// </param>
/// <param name="FieldValues">
/// Captured field values keyed by the schema field <c>Key</c>; filtered to keys that
/// actually exist in the schema (unknown keys the model invented are dropped).
/// </param>
/// <param name="Confidence">The model's self-reported confidence, clamped to [0, 1].</param>
/// <param name="Sentiment">
/// Optional free-form sentiment hint emitted by the model (e.g.
/// <c>positive|neutral|negative|very_negative</c>); <see langword="null"/> when absent.
/// </param>
/// <param name="ModelId">
/// Model identifier used to produce this classification (e.g. <c>gpt-4o-mini</c>).
/// Defaults to <c>"unknown"</c> when the provider does not expose a model name.
/// </param>
/// <param name="PromptVersion">
/// Classifier prompt template version (bumped in <see cref="DefaultTypificationAiClassifier"/>
/// whenever the system prompt changes). Defaults to <c>"unknown"</c>.
/// </param>
/// <param name="Usage">
/// LLM token usage reported by the provider for the classification call (A1), or
/// <see langword="null"/> when the provider returned no usage block. Drives the per-tenant
/// daily token budget (E3): the call's <see cref="LlmUsage.TotalTokens"/> is recorded against
/// the tenant's running UTC-day sum after a successful classification.
/// </param>
public sealed record AiClassification(
    IReadOnlyList<EntityId> NodePath,
    IReadOnlyDictionary<string, string> FieldValues,
    double Confidence,
    string? Sentiment,
    string ModelId = "unknown",
    string PromptVersion = "unknown",
    LlmUsage? Usage = null);
