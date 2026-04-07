using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantBrandingStoreTests
{
    private static InMemoryTenantBrandingStore CreateStore() => new();

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore();

        var result = await store.GetAsync("nonexistent-tenant");

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldInsertAndRetrieve_WhenNew()
    {
        var store = CreateStore();
        var branding = new TenantBranding
        {
            TenantId = "tenant-1",
            DisplayName = "Acme Corp",
            PrimaryColor = "#FF5733",
            Subdomain = "acme"
        };

        await store.UpsertAsync(branding);
        var result = await store.GetAsync("tenant-1");

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("tenant-1");
        result.DisplayName.Should().Be("Acme Corp");
        result.PrimaryColor.Should().Be("#FF5733");
        result.Subdomain.Should().Be("acme");
    }

    [Fact]
    public async Task GetBySubdomainAsync_ShouldResolve_WhenSubdomainExists()
    {
        var store = CreateStore();
        var branding = new TenantBranding
        {
            TenantId = "tenant-2",
            Subdomain = "beta"
        };
        await store.UpsertAsync(branding);

        var result = await store.GetBySubdomainAsync("BETA");

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("tenant-2");
    }

    [Fact]
    public async Task UpsertAsync_ShouldUpdate_WhenExists()
    {
        var store = CreateStore();
        var original = new TenantBranding
        {
            TenantId = "tenant-3",
            DisplayName = "Old Name",
            LogoUrl = "https://example.com/old-logo.png"
        };
        await store.UpsertAsync(original);

        var updated = new TenantBranding
        {
            TenantId = "tenant-3",
            DisplayName = "New Name",
            LogoUrl = "https://example.com/new-logo.png"
        };
        await store.UpsertAsync(updated);

        var result = await store.GetAsync("tenant-3");
        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("New Name");
        result.LogoUrl.Should().Be("https://example.com/new-logo.png");
    }
}
