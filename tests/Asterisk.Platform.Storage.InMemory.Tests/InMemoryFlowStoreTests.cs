using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryFlowStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static FlowDefinition MakeFlow(bool isPublished = false)
    {
        var nodeId = EntityId.New();
        return new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = Tenant,
            Name = "Test Flow",
            Version = 1,
            IsPublished = isPublished,
            EntryNodeId = nodeId,
            Nodes =
            [
                new FlowNode
                {
                    NodeId = nodeId,
                    Type = "message",
                    Config = new Dictionary<string, string>(),
                    Edges = [],
                },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task GetPublishedAsync_ShouldReturnFlow_WhenPublished()
    {
        var store = new InMemoryFlowStore();
        var flow = MakeFlow(isPublished: true);
        await store.SaveAsync(flow, CancellationToken.None);

        var result = await store.GetPublishedAsync(Tenant, flow.FlowId, CancellationToken.None);

        result.Should().BeSameAs(flow);
    }

    [Fact]
    public async Task GetPublishedAsync_ShouldReturnNull_WhenNotPublished()
    {
        var store = new InMemoryFlowStore();
        var flow = MakeFlow(isPublished: false);
        await store.SaveAsync(flow, CancellationToken.None);

        var result = await store.GetPublishedAsync(Tenant, flow.FlowId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnFlow_RegardlessOfPublishedState()
    {
        var store = new InMemoryFlowStore();
        var flow = MakeFlow(isPublished: false);
        await store.SaveAsync(flow, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, flow.FlowId, CancellationToken.None);

        result.Should().BeSameAs(flow);
    }
}
