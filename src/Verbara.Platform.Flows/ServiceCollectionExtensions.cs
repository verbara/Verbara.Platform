using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Verbara.Platform.Flows;

/// <summary>
/// DI registration extensions for Platform.Flows services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the flow execution engine and all built-in node handlers, plus a
    /// default-disabled <see cref="ILlmProvider"/> (<see cref="DisabledLlmProvider"/>) via
    /// <c>TryAddSingleton</c> so the AI node handlers compose; register a concrete
    /// <see cref="ILlmProvider"/> before this call to enable <c>ai_classify</c>/<c>ai_generate</c>.
    /// Callers must separately register:
    /// <list type="bullet">
    ///   <item><see cref="IFlowStore"/> — flow definition persistence.</item>
    ///   <item><see cref="IFlowExecutionStore"/> — execution state persistence.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPlatformFlows(this IServiceCollection services)
    {
        services.AddSingleton<ITemplateRenderer, TemplateRenderer>();
        services.AddSingleton<IFlowExecutionEngine, FlowExecutionEngine>();

        // Default-disabled LLM provider so the AI node handlers (ai_classify/ai_generate)
        // can be constructed and the engine composes even when no LLM backend is configured.
        // Non-AI flows never call it; an executed AI node fails loudly. A real provider
        // (future AI auto-disposition phase) wins by being registered before AddPlatformFlows
        // (TryAdd = default-that-can-be-overridden).
        services.TryAddSingleton<ILlmProvider, DisabledLlmProvider>();

        // Built-in node handlers.
        services.AddSingleton<IFlowNodeHandler, SendMessageNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, CollectInputNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, CollectReasonNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, ConditionNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, SetVariableNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, AiClassifyNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, AiGenerateNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, KnowledgeSearchNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, HttpRequestNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, EnqueueNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, WaitNodeHandler>();
        services.AddSingleton<IFlowNodeHandler, EndNodeHandler>();

        // HttpClient for HttpRequestNodeHandler.
        services.AddHttpClient<HttpRequestNodeHandler>();

        return services;
    }
}
