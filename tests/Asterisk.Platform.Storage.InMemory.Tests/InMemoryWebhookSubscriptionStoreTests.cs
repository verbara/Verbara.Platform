using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryWebhookSubscriptionStoreTests
{
    private readonly InMemoryWebhookSubscriptionStore _store = new();

    private static WebhookSubscription CreateSubscription(
        string? id = null, string tenantId = "t1", bool isActive = true,
        IReadOnlyList<string>? eventTypes = null) => new(
        SubscriptionId: id ?? Guid.NewGuid().ToString("N"),
        TenantId: tenantId,
        Name: "Test Webhook",
        EndpointUrl: "https://example.com/webhook",
        Secret: "test-secret-1234567890123456789012345678901234567890",
        EventTypes: eventTypes ?? ["conversation.message"],
        IsActive: isActive,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveAsync_ShouldPersist_WhenCalledWithNewSubscription()
    {
        var sub = CreateSubscription();
        await _store.SaveAsync(sub, CancellationToken.None);

        var result = await _store.GetByIdAsync(sub.SubscriptionId, CancellationToken.None);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Webhook");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetByIdAsync("nonexistent", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListByTenantAsync_ShouldReturnOnlyTenantSubscriptions()
    {
        await _store.SaveAsync(CreateSubscription(tenantId: "t1"), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(tenantId: "t1"), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(tenantId: "t2"), CancellationToken.None);

        var result = await _store.ListByTenantAsync("t1", CancellationToken.None);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetActiveByEventTypeAsync_ShouldFilterByActiveAndEventType()
    {
        await _store.SaveAsync(CreateSubscription(id: "s1", isActive: true,
            eventTypes: ["conversation.message", "agent.state_changed"]), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(id: "s2", isActive: false,
            eventTypes: ["conversation.message"]), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(id: "s3", isActive: true,
            eventTypes: ["campaign.status_changed"]), CancellationToken.None);

        var result = await _store.GetActiveByEventTypeAsync("t1", "conversation.message", CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].SubscriptionId.Should().Be("s1");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveSubscription()
    {
        var sub = CreateSubscription(id: "to-delete");
        await _store.SaveAsync(sub, CancellationToken.None);
        await _store.DeleteAsync("to-delete", CancellationToken.None);

        var result = await _store.GetByIdAsync("to-delete", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldUpsert_WhenSubscriptionAlreadyExists()
    {
        var sub = CreateSubscription(id: "upsert");
        await _store.SaveAsync(sub, CancellationToken.None);

        var updated = sub with { Name = "Updated" };
        await _store.SaveAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync("upsert", CancellationToken.None);
        result!.Name.Should().Be("Updated");
    }
}
