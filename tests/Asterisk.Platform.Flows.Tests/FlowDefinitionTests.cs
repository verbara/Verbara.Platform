using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests;

public sealed class FlowDefinitionTests
{
    [Fact]
    public void FlowDefinition_ShouldInitialise_WhenAllRequiredPropertiesProvided()
    {
        var flowId = EntityId.New();
        var tenantId = new TenantId("tenant-1");
        var entryId = EntityId.New();
        var node = new FlowNode
        {
            NodeId = entryId,
            Type = "end",
            Config = new Dictionary<string, string>(),
            Edges = [],
        };

        var flow = new FlowDefinition
        {
            FlowId = flowId,
            TenantId = tenantId,
            Name = "Greeting Flow",
            Version = 1,
            IsPublished = true,
            EntryNodeId = entryId,
            Nodes = [node],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        flow.FlowId.Should().Be(flowId);
        flow.TenantId.Should().Be(tenantId);
        flow.Name.Should().Be("Greeting Flow");
        flow.Version.Should().Be(1);
        flow.IsPublished.Should().BeTrue();
        flow.EntryNodeId.Should().Be(entryId);
        flow.Nodes.Should().HaveCount(1);
    }

    [Fact]
    public void FlowDefinition_ShouldImplementITenantScoped()
    {
        var flow = new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = new TenantId("t1"),
            Name = "Test",
            Version = 1,
            EntryNodeId = EntityId.New(),
            Nodes = [],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        flow.Should().BeAssignableTo<ITenantScoped>();
    }

    [Fact]
    public void FlowExecution_ShouldDefaultStatus_ToRunning()
    {
        var execution = new FlowExecution
        {
            ExecutionId = EntityId.New(),
            FlowId = EntityId.New(),
            FlowVersion = 1,
            TenantId = new TenantId("t1"),
            ConversationId = EntityId.New(),
            CurrentNodeId = EntityId.New(),
            StartedAt = DateTimeOffset.UtcNow,
        };

        execution.Status.Should().Be(FlowExecutionStatus.Running);
        execution.Variables.Should().BeEmpty();
        execution.StepCount.Should().Be(0);
    }

    [Fact]
    public void FlowNode_ShouldStoreConfigAndEdges()
    {
        var nodeId = EntityId.New();
        var targetId = EntityId.New();
        var node = new FlowNode
        {
            NodeId = nodeId,
            Type = "condition",
            Config = new Dictionary<string, string> { ["expression"] = "{{mood}} == \"happy\"" },
            Edges = [new FlowEdge("yes", targetId), new FlowEdge("no", targetId)],
        };

        node.NodeId.Should().Be(nodeId);
        node.Type.Should().Be("condition");
        node.Config["expression"].Should().Be("{{mood}} == \"happy\"");
        node.Edges.Should().HaveCount(2);
    }

    [Fact]
    public void FlowEdge_ShouldBeValueRecord()
    {
        var target = EntityId.New();
        var edge1 = new FlowEdge("yes", target);
        var edge2 = new FlowEdge("yes", target);

        edge1.Should().Be(edge2);
        edge1.Condition.Should().Be("yes");
        edge1.TargetNodeId.Should().Be(target);
    }
}
