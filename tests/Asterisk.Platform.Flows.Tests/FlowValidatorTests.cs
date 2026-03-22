using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests;

public sealed class FlowValidatorTests
{
    private static FlowNode MakeNode(EntityId id, string type, params FlowEdge[] edges) =>
        new()
        {
            NodeId = id,
            Type = type,
            Config = new Dictionary<string, string>(),
            Edges = edges,
        };

    private static FlowDefinition MakeFlow(EntityId entryId, params FlowNode[] nodes) =>
        new()
        {
            FlowId = EntityId.New(),
            TenantId = new TenantId("t1"),
            Name = "Test",
            Version = 1,
            EntryNodeId = entryId,
            Nodes = nodes,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void Validate_ShouldReturnValid_WhenSimpleLinearDag()
    {
        var n1 = EntityId.New();
        var n2 = EntityId.New();
        var flow = MakeFlow(n1,
            MakeNode(n1, "send_message", new FlowEdge("default", n2)),
            MakeNode(n2, "end"));

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldDetectCycle()
    {
        var n1 = EntityId.New();
        var n2 = EntityId.New();
        var flow = MakeFlow(n1,
            MakeNode(n1, "condition", new FlowEdge("yes", n2)),
            MakeNode(n2, "condition", new FlowEdge("yes", n1)));  // back edge → cycle

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainMatch("*cycle*");
    }

    [Fact]
    public void Validate_ShouldFailWhenEntryNodeMissing()
    {
        var n1 = EntityId.New();
        var missingEntry = EntityId.New();
        var flow = MakeFlow(missingEntry, MakeNode(n1, "end"));

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainMatch($"*{missingEntry}*");
    }

    [Fact]
    public void Validate_ShouldFailWhenEdgeTargetMissing()
    {
        var n1 = EntityId.New();
        var ghost = EntityId.New();
        var flow = MakeFlow(n1,
            MakeNode(n1, "condition", new FlowEdge("yes", ghost)));

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainMatch($"*{ghost}*");
    }

    [Fact]
    public void Validate_ShouldFailWhenNodeCountExceedsMax()
    {
        var entry = EntityId.New();
        var nodes = new List<FlowNode> { MakeNode(entry, "end") };
        // Add 100 more nodes (total = 101 > max 100).
        for (var i = 0; i < 100; i++)
            nodes.Add(MakeNode(EntityId.New(), "end"));

        var flow = MakeFlow(entry, [.. nodes]);
        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainMatch("*maximum*");
    }

    [Fact]
    public void Validate_ShouldFailForEmptyFlow()
    {
        var flow = MakeFlow(EntityId.New());  // no nodes

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_ShouldAllowDiamondDag()
    {
        // n1 → n2, n3 → n4  (diamond without a cycle)
        var n1 = EntityId.New();
        var n2 = EntityId.New();
        var n3 = EntityId.New();
        var n4 = EntityId.New();

        var flow = MakeFlow(n1,
            MakeNode(n1, "condition", new FlowEdge("yes", n2), new FlowEdge("no", n3)),
            MakeNode(n2, "send_message", new FlowEdge("default", n4)),
            MakeNode(n3, "send_message", new FlowEdge("default", n4)),
            MakeNode(n4, "end"));

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnMultipleErrors_WhenMultipleIssues()
    {
        var n1 = EntityId.New();
        var ghost1 = EntityId.New();
        var ghost2 = EntityId.New();
        var missingEntry = EntityId.New();

        var flow = MakeFlow(missingEntry,
            MakeNode(n1, "condition",
                new FlowEdge("yes", ghost1),
                new FlowEdge("no", ghost2)));

        var result = FlowValidator.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThan(1);
    }
}
