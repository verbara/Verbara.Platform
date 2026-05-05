using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Flows.Tests.Helpers;
using FluentAssertions;
using NSubstitute;

namespace Verbara.Platform.Flows.Tests.NodeHandlers;

public sealed class AiClassifyNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldCallLlm_AndReturnLabelAsEdgeCondition()
    {
        var llm = Substitute.For<ILlmProvider>();
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("billing"));

        var handler = new AiClassifyNodeHandler(llm, new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "ai_classify",
            new Dictionary<string, string>
            {
                ["system_prompt"] = "Classify as: billing, technical, other",
                ["labels"] = "billing,technical,other",
                ["variable"] = "intent",
            });

        var execution = MakeExecution();
        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("I need help with my invoice"), CancellationToken.None);

        result.NextEdgeCondition.Should().Be("billing");
        result.WaitForInput.Should().BeFalse();
        execution.Variables["intent"].Should().Be("billing");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassSystemPromptToLlm()
    {
        var llm = Substitute.For<ILlmProvider>();
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("technical"));

        var handler = new AiClassifyNodeHandler(llm, new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "ai_classify",
            new Dictionary<string, string> { ["system_prompt"] = "Classify this message" });

        await handler.ExecuteAsync(node, MakeExecution(), FlowTestHelpers.TextMessage("help"), CancellationToken.None);

        await llm.Received(1).CompleteAsync(
            Arg.Is<LlmRequest>(r => r.Messages[0].Role == "system" && r.Messages[0].Content == "Classify this message"),
            Arg.Any<CancellationToken>());
    }
}
