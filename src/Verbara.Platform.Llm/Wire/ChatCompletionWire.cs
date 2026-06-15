using System.Text.Json.Serialization;

namespace Verbara.Platform.Llm.Wire;

/// <summary>
/// Wire model for an OpenAI-compatible <c>POST /chat/completions</c> request body.
/// Property names are pinned with <see cref="JsonPropertyNameAttribute"/> so the on-the-wire
/// JSON is correct regardless of any source-gen naming policy (e.g. <c>max_tokens</c>).
/// </summary>
internal sealed record ChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("max_tokens")] int MaxTokens);

/// <summary>A single chat message turn on the OpenAI-compatible wire.</summary>
internal sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>Wire model for an OpenAI-compatible chat-completions response body.</summary>
internal sealed record ChatCompletionResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices,
    [property: JsonPropertyName("usage")] ChatUsage? Usage);

/// <summary>A single completion choice within a <see cref="ChatCompletionResponse"/>.</summary>
internal sealed record ChatChoice(
    [property: JsonPropertyName("message")] ChatMessage? Message);

/// <summary>Wire model for the token-usage block in an OpenAI-compatible response.</summary>
internal sealed record ChatUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
