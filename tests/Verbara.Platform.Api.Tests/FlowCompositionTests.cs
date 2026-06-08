using Verbara.Platform.Bot;
using Verbara.Platform.Flows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// DI composition smoke tests for the flow execution engine.
/// <para>
/// These would have caught the original CRITICAL FIX 1 bug: <see cref="ServiceCollectionExtensions.AddPlatformFlows"/>
/// was never called from the Api host, and even when called the engine could not be built because
/// <see cref="ILlmProvider"/> (required by the eagerly-constructed <c>ai_classify</c>/<c>ai_generate</c>
/// handlers) had no production registration. Resolving <see cref="IEnumerable{IFlowNodeHandler}"/>
/// forces the container to construct ALL node handlers, proving the whole graph composes against the
/// real Api container (incl. the default-disabled <see cref="DisabledLlmProvider"/>).
/// </para>
/// </summary>
public sealed class FlowCompositionTests : IDisposable
{
    private readonly PlatformApiFactory _factory;

    public FlowCompositionTests() => _factory = new PlatformApiFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Composition_ShouldResolveFlowEngineAndVirtualAgent_WhenApiHostBuilt()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var engine = sp.GetRequiredService<IFlowExecutionEngine>();
        var virtualAgent = sp.GetRequiredService<IVirtualAgent>();

        Assert.NotNull(engine);
        Assert.NotNull(virtualAgent);

        // Resolving the handler enumeration forces every handler ctor (incl. the AI ones,
        // which depend on the default-disabled ILlmProvider) to be constructed. All 12
        // built-in handlers must resolve without throwing.
        var handlers = sp.GetServices<IFlowNodeHandler>().ToList();
        Assert.Equal(12, handlers.Count);
    }

    [Fact]
    public void Composition_ShouldRegisterCollectReasonHandler_WhenApiHostBuilt()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var handlers = sp.GetServices<IFlowNodeHandler>().ToList();

        // collect_reason backs the P1 typification capture path (B1/B2); assert by NodeType
        // (locale-proof + does not require the handler's internal type).
        Assert.Contains(handlers, h => h.NodeType == "collect_reason");
        Assert.Contains(handlers, h => h.NodeType == "set_variable");
    }
}
