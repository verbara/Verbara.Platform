using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Flows.Tests.Helpers;
using Verbara.Platform.KnowledgeBase;
using FluentAssertions;
using NSubstitute;

namespace Verbara.Platform.Flows.Tests.NodeHandlers;

public sealed class KnowledgeSearchNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    private static Article MakeArticle(string title, string content) =>
        new()
        {
            ArticleId = EntityId.New(),
            TenantId = FlowTestHelpers.DefaultTenant,
            Title = title,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task ExecuteAsync_ShouldSearchAndStoreResults()
    {
        var search = Substitute.For<IKnowledgeSearch>();
        var article = MakeArticle("Refund Policy", "We offer 30-day refunds.");
        search.SearchAsync(
                Arg.Any<TenantId>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([new SearchResult(article, 0.95)]);

        var handler = new KnowledgeSearchNodeHandler(search, new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "knowledge_search",
            new Dictionary<string, string> { ["query"] = "{{user_question]}", ["max_results"] = "3" });

        var execution = MakeExecution();
        execution.Variables["user_question"] = "What is the refund policy?";

        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("found");
        result.WaitForInput.Should().BeFalse();
        execution.Variables["kb_result_count"].Should().Be("1");
        execution.Variables["kb_top_title"].Should().Be("Refund Policy");
        execution.Variables["kb_top_content"].Should().Be("We offer 30-day refunds.");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnNotFound_WhenNoResults()
    {
        var search = Substitute.For<IKnowledgeSearch>();
        search.SearchAsync(
                Arg.Any<TenantId>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var handler = new KnowledgeSearchNodeHandler(search, new TemplateRenderer());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "knowledge_search",
            new Dictionary<string, string> { ["query"] = "obscure topic" });

        var result = await handler.ExecuteAsync(node, MakeExecution(), null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("not_found");
    }
}
