using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>Smoke test of <see cref="WebhookDeliveryPostgresFixture"/> (webhook-delivery-runs-once, task 1.1).</summary>
[Collection(WebhookDeliveryPostgresSuite.Name)]
public sealed class WebhookDeliveryPostgresFixtureTests(WebhookDeliveryPostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetByIdAsync_ShouldReturnSeededActiveSubscriptionWithClosedCircuit_WhenFixtureStarted()
    {
        var seeded = await fixture.SubscriptionStore.GetByIdAsync(
            WebhookDeliveryPostgresFixture.SubscriptionId, CancellationToken.None);

        seeded.Should().NotBeNull();
        seeded!.TenantId.Should().Be(WebhookDeliveryPostgresFixture.TenantId);
        seeded.IsActive.Should().BeTrue();
        seeded.CircuitStatus.Should().Be(CircuitStatus.Closed);
        seeded.EndpointUrl.Should().StartWith("https://");
        seeded.EventTypes.Should().Equal(WebhookDeliveryPostgresFixture.EventType);

        var subscribed = await fixture.SubscriptionStore.GetActiveByEventTypeAsync(
            WebhookDeliveryPostgresFixture.TenantId, WebhookDeliveryPostgresFixture.EventType, CancellationToken.None);
        subscribed.Select(s => s.SubscriptionId).Should().Equal(WebhookDeliveryPostgresFixture.SubscriptionId);
    }
}
