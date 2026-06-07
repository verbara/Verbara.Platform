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

    [Fact]
    public async Task GetAsync_ShouldReturnDefault30_WhenPendingPauseTimeoutNotSet()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.PendingPauseTimeoutMinutes.Should().Be(30);
    }

    [Fact]
    public async Task SaveThenGet_ShouldRoundTripPendingPauseTimeoutMinutes_WhenCustomValue()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(
            new TenantAuthConfig { TenantId = "tenant-1", PendingPauseTimeoutMinutes = 45 },
            CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.PendingPauseTimeoutMinutes.Should().Be(45);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnDefault30_WhenWorkFailoverGraceNotSet()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.WorkFailoverGraceSeconds.Should().Be(30);
    }

    [Fact]
    public async Task SaveThenGet_ShouldRoundTripWorkFailoverGraceSeconds_WhenCustomValue()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(
            new TenantAuthConfig { TenantId = "tenant-1", WorkFailoverGraceSeconds = 90 },
            CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.WorkFailoverGraceSeconds.Should().Be(90);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnDefault25_WhenVoiceCallbackGraceNotSet()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.VoiceCallbackGraceSeconds.Should().Be(25);
    }

    [Fact]
    public async Task SaveThenGet_ShouldRoundTripVoiceCallbackGraceSeconds_WhenCustomValue()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(
            new TenantAuthConfig { TenantId = "tenant-1", VoiceCallbackGraceSeconds = 40 },
            CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.VoiceCallbackGraceSeconds.Should().Be(40);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnSeededDefaults_WhenRowHasNoCapacityColumns()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.MaxVoiceDefault.Should().Be(1);
        config.MaxChatDefault.Should().Be(3);
        config.MaxEmailDefault.Should().Be(5);
        config.MaxSmsDefault.Should().Be(3);
        config.MaxTotalDefault.Should().Be(5);
    }

    [Fact]
    public async Task TenantAuthConfig_ShouldRoundTripCapacityDefaults_WhenSaved()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(
            new TenantAuthConfig
            {
                TenantId = "tenant-1",
                MaxVoiceDefault = 2,
                MaxChatDefault = 6,
                MaxEmailDefault = 10,
                MaxSmsDefault = 4,
                MaxTotalDefault = 12,
            },
            CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);

        config.Should().NotBeNull();
        config!.MaxVoiceDefault.Should().Be(2);
        config.MaxChatDefault.Should().Be(6);
        config.MaxEmailDefault.Should().Be(10);
        config.MaxSmsDefault.Should().Be(4);
        config.MaxTotalDefault.Should().Be(12);
    }
}
