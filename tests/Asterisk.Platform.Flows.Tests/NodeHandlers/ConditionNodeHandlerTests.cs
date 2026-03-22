using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class ConditionNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldReturnYes_WhenVariableMatchesExpectedValue()
    {
        var handler = new ConditionNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "condition",
            new Dictionary<string, string> { ["expression"] = "{{mood}} == \"happy\"" });

        var execution = MakeExecution();
        execution.Variables["mood"] = "happy";

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("yes");
        result.WaitForInput.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnNo_WhenVariableDoesNotMatch()
    {
        var handler = new ConditionNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "condition",
            new Dictionary<string, string> { ["expression"] = "{{mood}} == \"happy\"" });

        var execution = MakeExecution();
        execution.Variables["mood"] = "sad";

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("no");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnNo_WhenExpressionUsesNotEquals()
    {
        var handler = new ConditionNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "condition",
            new Dictionary<string, string> { ["expression"] = "{{status}} != \"active\"" });

        var execution = MakeExecution();
        execution.Variables["status"] = "active";

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        // active != active → false → "no"
        result.NextEdgeCondition.Should().Be("no");
    }
}
