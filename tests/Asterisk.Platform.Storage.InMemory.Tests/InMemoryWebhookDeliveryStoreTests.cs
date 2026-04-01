using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryWebhookDeliveryStoreTests
{
    private readonly InMemoryWebhookDeliveryStore _store = new();

    private static WebhookDelivery CreateDelivery(
        string? id = null, string subId = "sub1", string tenantId = "t1",
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending,
        DateTimeOffset? nextRetryAt = null, int attempts = 0) => new(
        DeliveryId: id ?? Guid.NewGuid().ToString("N"),
        TenantId: tenantId,
        SubscriptionId: subId,
        EventType: "conversation.message",
        Payload: "{}",
        Status: status,
        Attempts: attempts,
        MaxAttempts: 8,
        NextRetryAt: nextRetryAt,
        LastResponseCode: null,
        LastError: null,
        CreatedAt: DateTimeOffset.UtcNow,
        DeliveredAt: null);

    [Fact]
    public async Task SaveAsync_ShouldPersist_WhenCalledWithNewDelivery()
    {
        var delivery = CreateDelivery(id: "d1");
        await _store.SaveAsync(delivery, CancellationToken.None);

        var result = await _store.GetByIdAsync("d1", CancellationToken.None);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ListPendingRetriesAsync_ShouldReturnOnlyDueDeliveries()
    {
        var now = DateTimeOffset.UtcNow;

        await _store.SaveAsync(CreateDelivery(id: "due", nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "future", nextRetryAt: now.AddMinutes(10)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "delivered",
            status: WebhookDeliveryStatus.Delivered, nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "no-retry",
            status: WebhookDeliveryStatus.Pending, nextRetryAt: null), CancellationToken.None);

        var result = await _store.ListPendingRetriesAsync(now, 100, CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].DeliveryId.Should().Be("due");
    }

    [Fact]
    public async Task ListPendingRetriesAsync_ShouldRespectBatchSize()
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
            await _store.SaveAsync(CreateDelivery(nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);

        var result = await _store.ListPendingRetriesAsync(now, 3, CancellationToken.None);
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListDeadLetterAsync_ShouldReturnPaginatedDeadLetters()
    {
        for (int i = 0; i < 5; i++)
            await _store.SaveAsync(CreateDelivery(tenantId: "t1",
                status: WebhookDeliveryStatus.DeadLetter), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(tenantId: "t2",
            status: WebhookDeliveryStatus.DeadLetter), CancellationToken.None);

        var result = await _store.ListDeadLetterAsync("t1", 1, 3, CancellationToken.None);
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceDelivery()
    {
        var delivery = CreateDelivery(id: "upd");
        await _store.SaveAsync(delivery, CancellationToken.None);

        var updated = delivery with { Status = WebhookDeliveryStatus.Delivered };
        await _store.UpdateAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync("upd", CancellationToken.None);
        result!.Status.Should().Be(WebhookDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task DeleteBySubscriptionAsync_ShouldRemoveAllDeliveriesForSubscription()
    {
        await _store.SaveAsync(CreateDelivery(id: "d1", subId: "sub-del"), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "d2", subId: "sub-del"), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "d3", subId: "sub-keep"), CancellationToken.None);

        await _store.DeleteBySubscriptionAsync("sub-del", CancellationToken.None);

        (await _store.GetByIdAsync("d1", CancellationToken.None)).Should().BeNull();
        (await _store.GetByIdAsync("d2", CancellationToken.None)).Should().BeNull();
        (await _store.GetByIdAsync("d3", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListBySubscriptionAsync_ShouldReturnPaginatedResults()
    {
        for (int i = 0; i < 5; i++)
            await _store.SaveAsync(CreateDelivery(subId: "sub-page"), CancellationToken.None);

        var result = await _store.ListBySubscriptionAsync("sub-page", 1, 3, CancellationToken.None);
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
    }
}
