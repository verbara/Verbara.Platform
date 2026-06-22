using System.Text.Json.Serialization;

namespace Verbara.Platform.Llm.Wire;

/// <summary>
/// Wire model for an Anthropic <c>POST /v1/messages</c> request body.
/// Property names are pinned with <see cref="JsonPropertyNameAttribute"/> so the on-the-wire
/// JSON is correct regardless of any source-gen naming policy (e.g. <c>max_tokens</c>).
/// <para>
/// Unlike the OpenAI Chat Completions contract, Anthropic carries the system prompt as a
/// <em>top-level</em> <c>system</c> string (hoisted out of <see cref="Messages"/>) rather than as
/// a <c>system</c>-role message turn.
/// </para>
/// </summary>
internal sealed record AnthropicMessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages,
    [property: JsonPropertyName("temperature")] double Temperature);

/// <summary>A single non-system message turn on the Anthropic Messages wire.</summary>
internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>Wire model for an Anthropic Messages response body.</summary>
internal sealed record AnthropicMessagesResponse(
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock>? Content,
    [property: JsonPropertyName("usage")] AnthropicUsage? Usage);

/// <summary>A single content block within an <see cref="AnthropicMessagesResponse"/>.</summary>
internal sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text);

/// <summary>Wire model for the token-usage block in an Anthropic Messages response.</summary>
internal sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);
