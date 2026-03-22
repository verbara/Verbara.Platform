using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class WaitAndEndNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    // ── WaitNodeHandler ───────────────────────────────────────────────────────

    [Fact]
    public async Task WaitNode_ShouldReturnWaitForInput_WhenNoMessageProvided()
    {
        var handler = new WaitNodeHandler();
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "wait");

        var result = await handler.ExecuteAsync(node, MakeExecution(), inboundMessage: null, CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();
    }

    [Fact]
    public async Task WaitNode_ShouldContinue_WhenMessageAlreadyPresent()
    {
        var handler = new WaitNodeHandler();
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "wait");
        var msg = FlowTestHelpers.TextMessage("hi");

        var result = await handler.ExecuteAsync(node, MakeExecution(), msg, CancellationToken.None);

        result.WaitForInput.Should().BeFalse();
        result.NextEdgeCondition.Should().Be("default");
    }

    // ── EndNodeHandler ────────────────────────────────────────────────────────

    [Fact]
    public async Task EndNode_ShouldSetStatusToCompleted()
    {
        var handler = new EndNodeHandler();
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "end");
        var execution = MakeExecution();

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        execution.Status.Should().Be(FlowExecutionStatus.Completed);
        execution.CompletedAt.Should().NotBeNull();
        result.NextEdgeCondition.Should().BeNull();
        result.WaitForInput.Should().BeFalse();
    }
}
