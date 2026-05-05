using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Verbara.Platform.Flows.Tests.NodeHandlers;

public sealed class EnqueueNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldSetHandedOff_AndStoreQueueId()
    {
        var handler = new EnqueueNodeHandler();
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "enqueue",
            new Dictionary<string, string> { ["queue_id"] = "support-1", ["priority"] = "High" });

        var execution = MakeExecution();
        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        execution.Status.Should().Be(FlowExecutionStatus.HandedOff);
        execution.Variables["__enqueue_queue_id"].Should().Be("support-1");
        execution.Variables["__enqueue_priority"].Should().Be("High");
        result.WaitForInput.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSetCompletedAt()
    {
        var handler = new EnqueueNodeHandler();
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "enqueue",
            new Dictionary<string, string> { ["queue_id"] = "q1" });

        var execution = MakeExecution();
        await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        execution.CompletedAt.Should().NotBeNull();
    }
}
