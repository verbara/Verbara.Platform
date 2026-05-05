using Verbara.Platform.Automation;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTimerStoreTests
{
    private static ScheduledTimer MakeTimer(DateTimeOffset fireAt, bool isFired = false) =>
        new ScheduledTimer
        {
            TimerId = EntityId.New(),
            TenantId = new TenantId("tenant-1"),
            ConversationId = EntityId.New(),
            CallbackRuleId = EntityId.New(),
            FireAt = fireAt,
            IsFired = isFired,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetOverdueAsync_ShouldReturnTimersThatAreOverdueAndNotFired()
    {
        var store = new InMemoryTimerStore();
        var now = DateTimeOffset.UtcNow;

        var overdue = MakeTimer(now.AddMinutes(-5));
        var future = MakeTimer(now.AddMinutes(5));
        var alreadyFired = MakeTimer(now.AddMinutes(-10), isFired: true);

        await store.SaveAsync(overdue, CancellationToken.None);
        await store.SaveAsync(future, CancellationToken.None);
        await store.SaveAsync(alreadyFired, CancellationToken.None);

        var result = await store.GetOverdueAsync(now, limit: 100, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].TimerId.Should().Be(overdue.TimerId);
    }

    [Fact]
    public async Task GetOverdueAsync_ShouldRespectLimit()
    {
        var store = new InMemoryTimerStore();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
            await store.SaveAsync(MakeTimer(now.AddMinutes(-i - 1)), CancellationToken.None);

        var result = await store.GetOverdueAsync(now, limit: 2, CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task MarkFiredAsync_ShouldSetIsFiredToTrue()
    {
        var store = new InMemoryTimerStore();
        var now = DateTimeOffset.UtcNow;
        var timer = MakeTimer(now.AddMinutes(-1));
        await store.SaveAsync(timer, CancellationToken.None);

        await store.MarkFiredAsync(timer, CancellationToken.None);

        var overdue = await store.GetOverdueAsync(now, limit: 100, CancellationToken.None);
        overdue.Should().BeEmpty();
    }
}
