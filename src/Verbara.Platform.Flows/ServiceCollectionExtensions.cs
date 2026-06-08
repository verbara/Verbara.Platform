using Verbara.Platform.Flows.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Flows;

/// <summary>
/// DI registration extensions for Platform.Flows services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the flow execution engine and all built-in node handlers.
    /// Callers must separately register:
    /// <list type="bullet">
    ///   <item><see cref="IFlowStore"/> — flow definition persistence.</item>
    ///   <item><see cref="IFlowExecutionStore"/> — execution state persistence.</item>
    ///   <item><see cref="ILlmProvider"/> — required for <c>ai_classify</c> and <c>ai_generate</c> nodes.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPlatformFlows(this IServiceCollection services)
    {
        services.AddSingleton<ITemplateRenderer, TemplateRenderer>();
        services.AddSingleton<IFlowExecutionEngine, FlowExecutionEngine>();

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
