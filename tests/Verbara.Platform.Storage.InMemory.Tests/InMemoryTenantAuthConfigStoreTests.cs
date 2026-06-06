using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTenantAuthConfigStoreTests
{
    [Fact]
    public async Task GetAsync_ShouldReturnDefault60_WhenAgentLivenessNotSet()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.AgentLivenessTimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public async Task SaveThenGet_ShouldRoundTripAgentLivenessTimeoutSeconds_WhenCustomValue()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(
            new TenantAuthConfig { TenantId = "tenant-1", AgentLivenessTimeoutSeconds = 90 },
            CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.AgentLivenessTimeoutSeconds.Should().Be(90);
    }
}
