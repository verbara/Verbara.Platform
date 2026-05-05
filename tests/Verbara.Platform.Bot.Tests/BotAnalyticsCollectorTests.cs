using Verbara.Platform.Bot;
using Verbara.Platform.Core;

namespace Verbara.Platform.Bot.Tests;

public class BotAnalyticsCollectorTests
{
    private readonly TenantId _tenantId = new("t1");
    private readonly EntityId _botId = EntityId.From("bot-001");
    private readonly EntityId _conversationId = EntityId.From("conv-001");

    [Fact]
    public void Emit_ShouldDeliverEventToSubscribers()
    {
        var collector = new BotAnalyticsCollector();
        BotAnalyticsEvent? received = null;
        collector.Events.Subscribe(e => received = e);

        var evt = new BotAnalyticsEvent(_botId, _tenantId, _conversationId,
            BotAnalyticsEventType.ConversationStarted, DateTimeOffset.UtcNow);
        collector.Emit(evt);

        received.Should().NotBeNull();
        received!.Type.Should().Be(BotAnalyticsEventType.ConversationStarted);
    }

    [Fact]
    public void Emit_ShouldDeliverMultipleEvents()
    {
        var collector = new BotAnalyticsCollector();
        var events = new List<BotAnalyticsEvent>();
        collector.Events.Subscribe(e => events.Add(e));

        collector.Emit(new BotAnalyticsEvent(_botId, _tenantId, _conversationId,
            BotAnalyticsEventType.ConversationStarted, DateTimeOffset.UtcNow));
        collector.Emit(new BotAnalyticsEvent(_botId, _tenantId, _conversationId,
            BotAnalyticsEventType.Resolved, DateTimeOffset.UtcNow));

        events.Should().HaveCount(2);
    }

    [Fact]
    public void Emit_ShouldDeliverHandedOffEvent()
    {
        var collector = new BotAnalyticsCollector();
        BotAnalyticsEvent? received = null;
        collector.Events.Subscribe(e => received = e);

        var evt = new BotAnalyticsEvent(_botId, _tenantId, _conversationId,
            BotAnalyticsEventType.HandedOff, DateTimeOffset.UtcNow,
            TurnCount: 5, HandoffReason: "Max turns");
        collector.Emit(evt);

        received!.TurnCount.Should().Be(5);
        received.HandoffReason.Should().Be("Max turns");
    }

    [Fact]
    public void Events_ShouldSupportMultipleSubscribers()
    {
        var collector = new BotAnalyticsCollector();
        var count1 = 0;
        var count2 = 0;
        collector.Events.Subscribe(_ => count1++);
        collector.Events.Subscribe(_ => count2++);

        collector.Emit(new BotAnalyticsEvent(_botId, _tenantId, _conversationId,
            BotAnalyticsEventType.Failed, DateTimeOffset.UtcNow));

        count1.Should().Be(1);
        count2.Should().Be(1);
    }
}
