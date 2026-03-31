using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTenantQuotaStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNotConfigured()
    {
        var store = new InMemoryTenantQuotaStore();

        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldPersistQuota()
    {
        var store = new InMemoryTenantQuotaStore();
        var quota = new TenantQuota
        {
            TenantId = Tenant1,
            MaxConcurrentChannels = 200,
            MaxMonthlyVoiceMinutes = 5000,
        };

        await store.UpsertAsync(quota, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);
        result.Should().NotBeNull();
        result!.MaxConcurrentChannels.Should().Be(200);
        result.MaxMonthlyVoiceMinutes.Should().Be(5000);
    }

    [Fact]
    public async Task UpsertAsync_ShouldOverwriteExisting()
    {
        var store = new InMemoryTenantQuotaStore();
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1, MaxActiveCampaigns = 5 }, CancellationToken.None);
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1, MaxActiveCampaigns = 50 }, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);
        result!.MaxActiveCampaigns.Should().Be(50);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveQuota()
    {
        var store = new InMemoryTenantQuotaStore();
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1 }, CancellationToken.None);

        await store.DeleteAsync(Tenant1, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotThrow_WhenNotExists()
    {
        var store = new InMemoryTenantQuotaStore();

        var act = () => store.DeleteAsync(Tenant1, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Stores_ShouldIsolateTenants()
    {
        var store = new InMemoryTenantQuotaStore();
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1, MaxActiveCampaigns = 10 }, CancellationToken.None);
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant2, MaxActiveCampaigns = 20 }, CancellationToken.None);

        var r1 = await store.GetAsync(Tenant1, CancellationToken.None);
        var r2 = await store.GetAsync(Tenant2, CancellationToken.None);

        r1!.MaxActiveCampaigns.Should().Be(10);
        r2!.MaxActiveCampaigns.Should().Be(20);
    }
}
