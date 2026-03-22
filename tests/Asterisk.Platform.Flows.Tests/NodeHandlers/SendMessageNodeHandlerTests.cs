using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class SendMessageNodeHandlerTests
{
    private readonly TemplateRenderer _renderer = new();

    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldRenderTemplate_AndStoreOutboundMessage()
    {
        var handler = new SendMessageNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "send_message",
            new Dictionary<string, string> { ["template"] = "Hello, {{name}}!" });

        var execution = MakeExecution();
        execution.Variables["name"] = "Bob";

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.WaitForInput.Should().BeFalse();
        result.NextEdgeCondition.Should().Be("default");
        execution.Variables.Values.Should().Contain("Hello, Bob!");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleMissingTemplate_Gracefully()
    {
        var handler = new SendMessageNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "send_message");

        var execution = MakeExecution();

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.WaitForInput.Should().BeFalse();
    }
}
