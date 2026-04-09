using Asterisk.Platform.Bot;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryBotAnalyticsStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static BotAnalyticsRecord MakeRecord(BotAnalyticsEventType type, int turns = 3, string? handoffReason = null) =>
        new()
        {
            EventType = type.ToString(),
            BotId = "bot-1",
            ConversationId = EntityId.New().Value,
            TurnCount = turns,
            HandoffReason = handoffReason,
            CreatedAt = Now,
        };

    [Fact]
    public async Task RecordAndSummary_ShouldCalculateRates()
    {
        var store = new InMemoryBotAnalyticsStore();

        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.ConversationStarted), CancellationToken.None);
        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.ConversationStarted), CancellationToken.None);
        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.Resolved, turns: 5), CancellationToken.None);
        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.HandedOff, turns: 8, handoffReason: "timeout"), CancellationToken.None);

        var summary = await store.GetSummaryAsync(Tenant1, Now.AddHours(-1), Now.AddHours(1), CancellationToken.None);

        summary.TotalConversations.Should().Be(2);
        summary.Resolved.Should().Be(1);
        summary.HandedOff.Should().Be(1);
        summary.ResolutionRate.Should().Be(0.5);
        summary.HandoffRate.Should().Be(0.5);
    }

    [Fact]
    public async Task Summary_ShouldReturnZeroes_WhenEmptyPeriod()
    {
        var store = new InMemoryBotAnalyticsStore();

        var summary = await store.GetSummaryAsync(Tenant1, Now.AddDays(-1), Now.AddDays(1), CancellationToken.None);

        summary.TotalConversations.Should().Be(0);
        summary.HandoffRate.Should().Be(0);
        summary.ResolutionRate.Should().Be(0);
    }

    [Fact]
    public async Task Summary_ShouldIsolateTenants()
    {
        var store = new InMemoryBotAnalyticsStore();

        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.ConversationStarted), CancellationToken.None);
        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.Resolved), CancellationToken.None);
        await store.RecordEventAsync(Tenant2, MakeRecord(BotAnalyticsEventType.ConversationStarted), CancellationToken.None);
        await store.RecordEventAsync(Tenant2, MakeRecord(BotAnalyticsEventType.Failed), CancellationToken.None);

        var summary1 = await store.GetSummaryAsync(Tenant1, Now.AddHours(-1), Now.AddHours(1), CancellationToken.None);
        var summary2 = await store.GetSummaryAsync(Tenant2, Now.AddHours(-1), Now.AddHours(1), CancellationToken.None);

        summary1.TotalConversations.Should().Be(1);
        summary1.Resolved.Should().Be(1);
        summary1.Failed.Should().Be(0);

        summary2.TotalConversations.Should().Be(1);
        summary2.Failed.Should().Be(1);
        summary2.Resolved.Should().Be(0);
    }

    [Fact]
    public async Task Summary_ShouldCountMaxTurnsReachedAsHandoff()
    {
        var store = new InMemoryBotAnalyticsStore();

        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.ConversationStarted), CancellationToken.None);
        await store.RecordEventAsync(Tenant1, MakeRecord(BotAnalyticsEventType.MaxTurnsReached, turns: 20), CancellationToken.None);

        var summary = await store.GetSummaryAsync(Tenant1, Now.AddHours(-1), Now.AddHours(1), CancellationToken.None);

        summary.HandedOff.Should().Be(1);
        summary.HandoffRate.Should().Be(1.0);
    }
}
