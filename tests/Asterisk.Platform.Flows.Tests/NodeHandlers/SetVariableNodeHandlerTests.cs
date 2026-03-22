using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class SetVariableNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    [Fact]
    public async Task ExecuteAsync_ShouldStoreVariable_WithRenderedValue()
    {
        var handler = new SetVariableNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "set_variable",
            new Dictionary<string, string>
            {
                ["variable"] = "greeting",
                ["value"] = "Hi {{name}}!",
            });

        var execution = MakeExecution();
        execution.Variables["name"] = "Carol";

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("default");
        result.WaitForInput.Should().BeFalse();
        execution.Variables["greeting"].Should().Be("Hi Carol!");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldOverwriteExistingVariable()
    {
        var handler = new SetVariableNodeHandler(new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "set_variable",
            new Dictionary<string, string> { ["variable"] = "x", ["value"] = "new" });

        var execution = MakeExecution();
        execution.Variables["x"] = "old";

        await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        execution.Variables["x"].Should().Be("new");
    }
}
