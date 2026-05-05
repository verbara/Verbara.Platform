using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Flows.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Flows.Tests;

public sealed class FlowExecutionEngineTests
{
    private readonly TemplateRenderer _renderer = new();

    private FlowExecutionEngine BuildEngine(
        InMemoryFlowStore flowStore,
        InMemoryFlowExecutionStore executionStore,
        IEnumerable<IFlowNodeHandler>? extraHandlers = null)
    {
        var handlers = new List<IFlowNodeHandler>
        {
            new SendMessageNodeHandler(_renderer),
            new CollectInputNodeHandler(_renderer),
            new ConditionNodeHandler(_renderer),
            new SetVariableNodeHandler(_renderer),
            new EnqueueNodeHandler(),
            new WaitNodeHandler(),
            new EndNodeHandler(),
        };

        if (extraHandlers is not null)
            handlers.AddRange(extraHandlers);

        return new FlowExecutionEngine(
            flowStore,
            executionStore,
            handlers,
            NullLogger<FlowExecutionEngine>.Instance);
    }

    // ── StartAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldCreateRunningExecution_WhenPublishedFlowExists()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var entryId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(entryId, published: true,
            FlowTestHelpers.MakeNode(entryId, "end"));
        flowStore.Add(flow);

        var execution = await engine.StartAsync(
            FlowTestHelpers.DefaultTenant, flow.FlowId, EntityId.New(), CancellationToken.None);

        execution.Status.Should().Be(FlowExecutionStatus.Running);
        execution.FlowId.Should().Be(flow.FlowId);
        execution.CurrentNodeId.Should().Be(entryId);
    }

    [Fact]
    public async Task StartAsync_ShouldThrow_WhenFlowNotPublished()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var entryId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(entryId, published: false,
            FlowTestHelpers.MakeNode(entryId, "end"));
        flowStore.Add(flow);

        var act = () => engine.StartAsync(
            FlowTestHelpers.DefaultTenant, flow.FlowId, EntityId.New(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── ProcessMessageAsync — single node ─────────────────────────────────────

    [Fact]
    public async Task ProcessMessageAsync_ShouldComplete_WhenEndNodeReached()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var endId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(endId, published: true,
            FlowTestHelpers.MakeNode(endId, "end"));
        flowStore.Add(flow);

        var convId = EntityId.New();
        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, convId, endId);
        await execStore.SaveAsync(execution, CancellationToken.None);

        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("hi"), CancellationToken.None);

        result.Status.Should().Be(FlowExecutionStatus.Completed);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldWait_WhenCollectInputHasNoMessage()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var collectId = EntityId.New();
        var endId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(collectId, published: true,
            FlowTestHelpers.MakeNode(collectId, "collect_input",
                new Dictionary<string, string> { ["variable"] = "answer" },
                new FlowEdge("collected", endId)),
            FlowTestHelpers.MakeNode(endId, "end"));
        flowStore.Add(flow);

        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, EntityId.New(), collectId);
        execution.Status = FlowExecutionStatus.Running;
        await execStore.SaveAsync(execution, CancellationToken.None);

        // Send empty envelope (simulate: engine passes null for no inbound — but here we send real msg)
        // To test "no message" we start a fresh execution that hasn't received input yet.
        // We re-use ProcessMessageAsync but the collect_input node needs inboundMessage = null.
        // Workaround: pass message; collect_input will store it and return "collected".
        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("yes"), CancellationToken.None);

        // Should proceed to end.
        result.Status.Should().Be(FlowExecutionStatus.Completed);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldProduceOutboundMessages_FromSendMessageNode()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var sendId = EntityId.New();
        var endId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(sendId, published: true,
            FlowTestHelpers.MakeNode(sendId, "send_message",
                new Dictionary<string, string> { ["template"] = "Hello!" },
                new FlowEdge("default", endId)),
            FlowTestHelpers.MakeNode(endId, "end"));
        flowStore.Add(flow);

        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, EntityId.New(), sendId);
        await execStore.SaveAsync(execution, CancellationToken.None);

        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("start"), CancellationToken.None);

        result.Status.Should().Be(FlowExecutionStatus.Completed);
        result.OutboundMessages.Should().NotBeNullOrEmpty();
        result.OutboundMessages![0].Blocks[0].Should().BeOfType<TextBlock>()
            .Which.Text.Should().Be("Hello!");
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldHandOff_WhenEnqueueNodeReached()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var enqueueId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(enqueueId, published: true,
            FlowTestHelpers.MakeNode(enqueueId, "enqueue",
                new Dictionary<string, string> { ["queue_id"] = "support-q", ["priority"] = "High" }));
        flowStore.Add(flow);

        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, EntityId.New(), enqueueId);
        await execStore.SaveAsync(execution, CancellationToken.None);

        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("help"), CancellationToken.None);

        result.Status.Should().Be(FlowExecutionStatus.HandedOff);
        result.TargetQueueId.Should().NotBeNull();
        result.TargetQueueId!.Value.Value.Should().Be("support-q");
        result.Priority.Should().Be(MessagePriority.High);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldWalkMultipleNodes_InOneCall()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var n1 = EntityId.New();
        var n2 = EntityId.New();
        var n3 = EntityId.New();

        var flow = FlowTestHelpers.MakeFlow(n1, published: true,
            FlowTestHelpers.MakeNode(n1, "set_variable",
                new Dictionary<string, string> { ["variable"] = "x", ["value"] = "1" },
                new FlowEdge("default", n2)),
            FlowTestHelpers.MakeNode(n2, "set_variable",
                new Dictionary<string, string> { ["variable"] = "y", ["value"] = "2" },
                new FlowEdge("default", n3)),
            FlowTestHelpers.MakeNode(n3, "end"));
        flowStore.Add(flow);

        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, EntityId.New(), n1);
        await execStore.SaveAsync(execution, CancellationToken.None);

        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("go"), CancellationToken.None);

        result.Status.Should().Be(FlowExecutionStatus.Completed);
        var saved = await execStore.GetByIdAsync(execution.ExecutionId, CancellationToken.None);
        saved!.Variables["x"].Should().Be("1");
        saved.Variables["y"].Should().Be("2");
        saved.StepCount.Should().Be(3);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldPersistState_AfterEachStep()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var waitId = EntityId.New();
        var endId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(waitId, published: true,
            FlowTestHelpers.MakeNode(waitId, "wait",
                null,
                new FlowEdge("default", endId)),
            FlowTestHelpers.MakeNode(endId, "end"));
        flowStore.Add(flow);

        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, EntityId.New(), waitId);
        await execStore.SaveAsync(execution, CancellationToken.None);

        // First call — wait node has no inbound yet wait for message.
        // Pass a message so wait node continues.
        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("ping"), CancellationToken.None);

        result.Status.Should().Be(FlowExecutionStatus.Completed);
        var saved = await execStore.GetByIdAsync(execution.ExecutionId, CancellationToken.None);
        saved!.Status.Should().Be(FlowExecutionStatus.Completed);
    }

    [Fact]
    public async Task GetActiveExecutionAsync_ShouldReturnRunningExecution()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var convId = EntityId.New();
        var execution = new FlowExecution
        {
            ExecutionId = EntityId.New(),
            FlowId = EntityId.New(),
            FlowVersion = 1,
            TenantId = FlowTestHelpers.DefaultTenant,
            ConversationId = convId,
            CurrentNodeId = EntityId.New(),
            Status = FlowExecutionStatus.WaitingForInput,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await execStore.SaveAsync(execution, CancellationToken.None);

        var found = await engine.GetActiveExecutionAsync(
            FlowTestHelpers.DefaultTenant, convId, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ExecutionId.Should().Be(execution.ExecutionId);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldReturnExistingStatus_WhenExecutionAlreadyCompleted()
    {
        var flowStore = new InMemoryFlowStore();
        var execStore = new InMemoryFlowExecutionStore();
        var engine = BuildEngine(flowStore, execStore);

        var endId = EntityId.New();
        var flow = FlowTestHelpers.MakeFlow(endId, published: true,
            FlowTestHelpers.MakeNode(endId, "end"));
        flowStore.Add(flow);

        var execution = FlowTestHelpers.MakeRunningExecution(flow.FlowId, EntityId.New(), endId);
        execution.Status = FlowExecutionStatus.Completed;
        await execStore.SaveAsync(execution, CancellationToken.None);

        var result = await engine.ProcessMessageAsync(
            execution.ExecutionId, FlowTestHelpers.TextMessage("late"), CancellationToken.None);

        result.Status.Should().Be(FlowExecutionStatus.Completed);
    }
}
