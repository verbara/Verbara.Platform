using System.Text.Json.Serialization;
using Verbara.Platform.Llm.Wire;

namespace Verbara.Platform.Llm;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the Anthropic Messages API wire DTOs.
/// Required for Native AOT — no reflection-based (de)serialization is permitted. Kept separate
/// from <see cref="LlmJsonContext"/> (the OpenAI-compatible wire) because the Anthropic contract
/// differs (top-level <c>system</c>, <c>input_tokens</c>/<c>output_tokens</c>).
/// <para>
/// The naming policy is snake_case as a defensive default, but every wire property is also pinned
/// with an explicit <c>[JsonPropertyName]</c> so the contract holds independent of the policy.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicMessagesResponse))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicContentBlock))]
[JsonSerializable(typeof(AnthropicUsage))]
internal sealed partial class AnthropicJsonContext : JsonSerializerContext;
