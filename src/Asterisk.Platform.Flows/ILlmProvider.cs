namespace Asterisk.Platform.Flows;

/// <summary>
/// Abstraction over a large-language-model completion endpoint.
/// Register a concrete implementation to enable AI nodes (ai_classify, ai_generate).
/// </summary>
public interface ILlmProvider
{
    /// <summary>Sends a completion request and returns the model's response.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>A completion request to an LLM.</summary>
/// <param name="Messages">The conversation turns sent to the model.</param>
/// <param name="Temperature">Sampling temperature (0–1). Defaults to 0.7.</param>
/// <param name="MaxTokens">Maximum tokens in the response. Defaults to 500.</param>
public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    double Temperature = 0.7,
    int MaxTokens = 500);

/// <summary>A single message turn in an LLM conversation.</summary>
/// <param name="Role">The role of the author — typically "system", "user", or "assistant".</param>
/// <param name="Content">The text content of the message.</param>
public sealed record LlmMessage(string Role, string Content);

/// <summary>The completion text returned by an LLM.</summary>
/// <param name="Content">The model's response text.</param>
public sealed record LlmResponse(string Content);
