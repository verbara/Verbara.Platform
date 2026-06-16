using System.Text.Json.Serialization;
using Verbara.Platform.Llm.Wire;

namespace Verbara.Platform.Llm;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the OpenAI-compatible chat-completions
/// wire DTOs. Required for Native AOT — no reflection-based (de)serialization is permitted.
/// <para>
/// The naming policy is snake_case as a defensive default, but every wire property is also pinned
/// with an explicit <c>[JsonPropertyName]</c> so the contract holds independent of the policy.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatChoice))]
[JsonSerializable(typeof(ChatUsage))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;
