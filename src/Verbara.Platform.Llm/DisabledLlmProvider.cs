namespace Verbara.Platform.Llm;

/// <summary>
/// Default, disabled <see cref="ILlmProvider"/> registered by <c>AddPlatformFlows</c>
/// so the flow engine and its full handler graph (including the AI nodes
/// <c>ai_classify</c>/<c>ai_generate</c>) can be composed even when no LLM backend is configured.
/// <para>
/// It constructs with no dependencies and never produces a completion: every call throws
/// <see cref="InvalidOperationException"/>. Non-AI flows never touch it, so they run normally;
/// an AI node executed in an unconfigured tenant fails loudly rather than silently mis-routing
/// on a fabricated classification.
/// </para>
/// <para>
/// A real provider (future AI auto-disposition phase) overrides this default — it is registered
/// via <c>TryAddSingleton</c>, so registering a concrete <see cref="ILlmProvider"/> before
/// <c>AddPlatformFlows</c> takes precedence.
/// </para>
/// </summary>
public sealed class DisabledLlmProvider : ILlmProvider
{
    private const string DisabledMessage =
        "AI flow nodes (ai_classify/ai_generate) require an ILlmProvider implementation; " +
        "none is configured. Configure an LLM provider to enable AI nodes.";

    /// <inheritdoc />
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        throw new InvalidOperationException(DisabledMessage);
}
