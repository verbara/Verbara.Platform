using Verbara.Platform.Api.Services.Reports;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Reports;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Analytics;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Reports;

public class ReportDataBuilderTests
{
    private static readonly DateTimeOffset From = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 4, 30, 23, 59, 59, TimeSpan.Zero);

    // ── Registry ────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_ShouldThrow_WhenUnknownType()
    {
        var registry = new ReportDataBuilderRegistry([]);
        registry.TryGetBuilder("nonexistent", out _).Should().BeFalse();
    }

    [Fact]
    public void Registry_ShouldResolveCorrectBuilder_WhenRegistered()
    {
        var builder = Substitute.For<IReportDataBuilder>();
        builder.ReportType.Returns("test_report");

        var registry = new ReportDataBuilderRegistry([builder]);
        registry.TryGetBuilder("test_report", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(builder);
    }

    [Fact]
    public void Registry_ShouldBeCaseInsensitive()
    {
        var builder = Substitute.For<IReportDataBuilder>();
        builder.ReportType.Returns("Agent_Performance");

        var registry = new ReportDataBuilderRegistry([builder]);
        registry.TryGetBuilder("agent_performance", out _).Should().BeTrue();
    }

    // ── AgentPerformanceReportBuilder ───────────────────────────────────────────

    [Fact]
    public async Task AgentPerformance_ShouldGenerateRowsPerAgent()
    {
        var store = Substitute.For<IIntervalSnapshotStore>();
        store.QueryAgentAsync("tenant-1", From, To, null, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSnapshot>
            {
                new() { AgentId = "agent-1", ServerId = "s1", IntervalStart = From, CallsHandled = 10,
                    TotalTalkMs = 50000, TotalHoldMs = 5000, TotalAcwMs = 3000, RnaCount = 1, Transfers = 2, TotalPauseMs = 60000, LoginDurationMs = 360000 },
                new() { AgentId = "agent-2", ServerId = "s1", IntervalStart = From, CallsHandled = 5,
                    TotalTalkMs = 25000, TotalHoldMs = 2000, TotalAcwMs = 1000, RnaCount = 0, Transfers = 1, TotalPauseMs = 30000, LoginDurationMs = 180000 },
            });

        var builder = new AgentPerformanceReportBuilder(store);
        var data = await builder.BuildAsync("tenant-1", From, To, null, CancellationToken.None);

        data.ReportType.Should().Be("agent_performance");
        data.Rows.Should().HaveCount(2);
        data.Summary.Should().ContainKey("Total Calls Handled");
        data.Summary!["Total Calls Handled"].Should().Be(15);
    }

    [Fact]
    public async Task AgentPerformance_ShouldReturnEmptyRows_WhenNoData()
    {
        var store = Substitute.For<IIntervalSnapshotStore>();
        store.QueryAgentAsync("tenant-1", From, To, null, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSnapshot>());

        var builder = new AgentPerformanceReportBuilder(store);
        var data = await builder.BuildAsync("tenant-1", From, To, null, CancellationToken.None);

        data.Rows.Should().BeEmpty();
        data.Summary!["Total Calls Handled"].Should().Be(0);
    }

    // ── QueueAnalyticsReportBuilder ─────────────────────────────────────────────

    [Fact]
    public async Task QueueAnalytics_ShouldGenerateRowsPerQueue()
    {
        var store = Substitute.For<IIntervalSnapshotStore>();
        store.QueryAsync("tenant-1", From, To, null, null, Arg.Any<CancellationToken>())
            .Returns(new List<IntervalSnapshot>
            {
                new() { QueueName = "support", ServerId = "s1", IntervalStart = From,
                    CallsOffered = 100, CallsAnswered = 90, CallsAbandoned = 10, SlaMetCount = 80,
                    TotalWaitMs = 180000, TotalTalkMs = 900000, TotalHoldMs = 50000, TotalAcwMs = 30000 },
                new() { QueueName = "sales", ServerId = "s1", IntervalStart = From,
                    CallsOffered = 50, CallsAnswered = 45, CallsAbandoned = 5, SlaMetCount = 40,
                    TotalWaitMs = 90000, TotalTalkMs = 450000, TotalHoldMs = 25000, TotalAcwMs = 15000 },
            });

        var builder = new QueueAnalyticsReportBuilder(store);
        var data = await builder.BuildAsync("tenant-1", From, To, null, CancellationToken.None);

        data.ReportType.Should().Be("queue_analytics");
        data.Rows.Should().HaveCount(2);
        data.Summary!["Total Offered"].Should().Be(150);
        data.Summary["Total Answered"].Should().Be(135);
    }

    [Fact]
    public async Task QueueAnalytics_ShouldReturnEmptyRows_WhenNoData()
    {
        var store = Substitute.For<IIntervalSnapshotStore>();
        store.QueryAsync("tenant-1", From, To, null, null, Arg.Any<CancellationToken>())
            .Returns(new List<IntervalSnapshot>());

        var builder = new QueueAnalyticsReportBuilder(store);
        var data = await builder.BuildAsync("tenant-1", From, To, null, CancellationToken.None);

        data.Rows.Should().BeEmpty();
        data.Summary!["Total Offered"].Should().Be(0);
    }

    // ── ConversationSummaryReportBuilder ─────────────────────────────────────────

    [Fact]
    public async Task ConversationSummary_ShouldGroupByChannel()
    {
        var convStore = new InMemoryConversationStore();
        var msgStore = new InMemoryMessageStore();
        var tid = new TenantId("tenant-1");

        var conv1 = new Conversation
        {
            ConversationId = EntityId.New(), TenantId = tid, ContactId = EntityId.New(),
            Channel = ChannelType.WebChat, State = ConversationState.Active,
            CreatedAt = From.AddDays(1),
        };
        var conv2 = new Conversation
        {
            ConversationId = EntityId.New(), TenantId = tid, ContactId = EntityId.New(),
            Channel = ChannelType.Email, State = ConversationState.Closed,
            CreatedAt = From.AddDays(2), ClosedAt = From.AddDays(2).AddHours(4),
        };

        await convStore.SaveAsync(conv1, CancellationToken.None);
        await convStore.SaveAsync(conv2, CancellationToken.None);

        var builder = new ConversationSummaryReportBuilder(convStore, msgStore);
        var data = await builder.BuildAsync("tenant-1", From, To, null, CancellationToken.None);

        data.ReportType.Should().Be("conversation_summary");
        data.Rows.Should().HaveCount(2);
        data.Summary!["Total Conversations"].Should().Be(2);
    }

    [Fact]
    public async Task ConversationSummary_ShouldReturnEmptyRows_WhenNoData()
    {
        var convStore = new InMemoryConversationStore();
        var msgStore = new InMemoryMessageStore();

        var builder = new ConversationSummaryReportBuilder(convStore, msgStore);
        var data = await builder.BuildAsync("tenant-1", From, To, null, CancellationToken.None);

        data.Rows.Should().BeEmpty();
        data.Summary!["Total Conversations"].Should().Be(0);
    }
}
