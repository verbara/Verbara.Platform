using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class CollectInputNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldWaitForInput_WhenNoMessageProvided()
    {
        var handler = new CollectInputNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "collect_input",
            new Dictionary<string, string> { ["variable"] = "answer" });

        var result = await handler.ExecuteAsync(node, MakeExecution(), inboundMessage: null, CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStoreInput_WhenValidMessageProvided()
    {
        var handler = new CollectInputNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "collect_input",
            new Dictionary<string, string> { ["variable"] = "city" });

        var execution = MakeExecution();
        var msg = FlowTestHelpers.TextMessage("London");

        var result = await handler.ExecuteAsync(node, execution, msg, CancellationToken.None);

        result.WaitForInput.Should().BeFalse();
        result.NextEdgeCondition.Should().Be("collected");
        execution.Variables["city"].Should().Be("London");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryAndWait_WhenValidationFails()
    {
        var nodeId = EntityId.New();
        var handler = new CollectInputNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(nodeId, "collect_input",
            new Dictionary<string, string>
            {
                ["variable"] = "code",
                ["validator"] = @"^\d{4}$",  // must be 4 digits
                ["retry_prompt"] = "Please enter a 4-digit code.",
                ["max_retries"] = "3",
            });

        var execution = MakeExecution();
        var msg = FlowTestHelpers.TextMessage("abc");  // invalid

        var result = await handler.ExecuteAsync(node, execution, msg, CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();
        // Retry counter should have incremented.
        execution.Variables[$"__collect_retries_{nodeId}"].Should().Be("1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAcceptInput_AfterFailedRetry()
    {
        var nodeId = EntityId.New();
        var handler = new CollectInputNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(nodeId, "collect_input",
            new Dictionary<string, string>
            {
                ["variable"] = "code",
                ["validator"] = @"^\d{4}$",
            });

        var execution = MakeExecution();
        execution.Variables[$"__collect_retries_{nodeId}"] = "1";  // already tried once

        var validMsg = FlowTestHelpers.TextMessage("1234");
        var result = await handler.ExecuteAsync(node, execution, validMsg, CancellationToken.None);

        result.WaitForInput.Should().BeFalse();
        result.NextEdgeCondition.Should().Be("collected");
        execution.Variables["code"].Should().Be("1234");
    }
}
