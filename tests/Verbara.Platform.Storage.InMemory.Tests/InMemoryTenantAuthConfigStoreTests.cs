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

    // conversations-close-after-inactivity task 1.4 (design D11): the per-tenant inactivity window override is read
    // by GetAsync and never written by the whole-row SaveAsync; a test seeds it the way an operator's SQL sets it.

    [Fact]
    public async Task GetAsync_ShouldReadTheSeededOverride_WhenSeeded()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);
        store.SeedInactivityCloseAfterHours("tenant-1", 48);

        (await store.GetAsync("tenant-1", CancellationToken.None))!.InactivityCloseAfterHours.Should().Be(48);
    }

    [Fact]
    public async Task SaveAsync_ShouldNotWriteTheOverride_WhenANewRowCarriesOne()
    {
        var store = new InMemoryTenantAuthConfigStore();

        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1", InactivityCloseAfterHours = 24 }, CancellationToken.None);

        (await store.GetAsync("tenant-1", CancellationToken.None))!.InactivityCloseAfterHours.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheStoredOverride_WhenTheSavedEntityCarriesAnother()
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = "tenant-1" }, CancellationToken.None);
        store.SeedInactivityCloseAfterHours("tenant-1", 48);

        await store.SaveAsync(
            new TenantAuthConfig { TenantId = "tenant-1", InactivityCloseAfterHours = 24, PasswordMinLength = 16 },
            CancellationToken.None);

        var config = await store.GetAsync("tenant-1", CancellationToken.None);
        config!.InactivityCloseAfterHours.Should().Be(48, "the whole-row save never writes the column (D11)");
        config.PasswordMinLength.Should().Be(16);
    }
}
