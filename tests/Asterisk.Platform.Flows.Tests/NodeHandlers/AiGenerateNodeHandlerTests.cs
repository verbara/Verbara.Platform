using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class AiGenerateNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldCallLlm_AndStoreResponseAsOutbound()
    {
        var llm = Substitute.For<ILlmProvider>();
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("Sure, I can help you with that."));

        var handler = new AiGenerateNodeHandler(llm, new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "ai_generate",
            new Dictionary<string, string> { ["system_prompt"] = "You are a helpful assistant." });

        var execution = MakeExecution();
        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("Can you help?"), CancellationToken.None);

        result.NextEdgeCondition.Should().Be("default");
        result.WaitForInput.Should().BeFalse();
        execution.Variables["__last_outbound"].Should().Be("Sure, I can help you with that.");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRenderSystemPromptWithVariables()
    {
        var llm = Substitute.For<ILlmProvider>();
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("Response"));

        var handler = new AiGenerateNodeHandler(llm, new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "ai_generate",
            new Dictionary<string, string> { ["system_prompt"] = "Help {{name}} with their issue." });

        var execution = MakeExecution();
        execution.Variables["name"] = "Alice";

        await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        await llm.Received(1).CompleteAsync(
            Arg.Is<LlmRequest>(r => r.Messages[0].Content == "Help Alice with their issue."),
            Arg.Any<CancellationToken>());
    }
}
