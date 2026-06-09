using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Source-generated (reflection-free, Native AOT-safe) JSON context for PARSING the
/// direct-classifier LLM response. The model is prompted to emit a single JSON object
/// with the lower/camelCase shape of <see cref="AiClassificationResult"/>; this context
/// deserializes it via <c>TypificationAiJsonContext.Default.AiClassificationResult</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AiClassificationResult))]
internal sealed partial class TypificationAiJsonContext : JsonSerializerContext;

/// <summary>
/// The raw LLM parse shape (model-emitted JSON). Property names are lower/camelCase to
/// match exactly what the prompt instructs the model to produce. This is an internal
/// transport DTO — the validated public result is <see cref="AiClassification"/>.
/// </summary>
/// <param name="leafCode">The chosen leaf node <c>Code</c> (validated against the schema).</param>
/// <param name="confidence">Self-reported confidence (clamped to [0, 1] after parse).</param>
/// <param name="sentiment">Free-form sentiment hint, or <see langword="null"/>.</param>
/// <param name="fields">Captured field values keyed by field <c>Key</c>, or <see langword="null"/>.</param>
internal sealed record AiClassificationResult(
    string? leafCode,
    double confidence,
    string? sentiment,
    IReadOnlyDictionary<string, string>? fields);
