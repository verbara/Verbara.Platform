using System.Text.Json.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>supervisor-silent-listen 1.2 (design D3, D8, D12): the per-user listen profile, in memory.</summary>
public sealed class InMemorySupervisorListenProfileStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly TenantId Other = new("tenant-2");
    private static readonly DateTimeOffset At = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private static SupervisorListenProfile Profile(TenantId tenant, string userId, string? extension = "sl0a1b2c3d",
        string? password = "pw", string? phone = null) => new()
    {
        TenantId = tenant,
        UserId = EntityId.From(userId),
        SipExtension = extension,
        SipPassword = password,
        PhoneNumber = phone,
        CreatedAt = At,
        UpdatedAt = At,
    };

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNoProfile()
    {
        var store = new InMemorySupervisorListenProfileStore();

        (await store.GetAsync(Tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripAndUpsert_ByTenantAndUser()
    {
        var store = new InMemorySupervisorListenProfileStore();

        (await store.SaveAsync(Profile(Tenant, "u1"), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Profile(Tenant, "u1", phone: "+573001234567"), CancellationToken.None)).Should().BeTrue();

        var read = await store.GetAsync(Tenant, EntityId.From("u1"), CancellationToken.None);
        read!.SipExtension.Should().Be("sl0a1b2c3d");
        read.SipPassword.Should().Be("pw");
        read.PhoneNumber.Should().Be("+573001234567");
        (await store.GetAsync(Other, EntityId.From("u1"), CancellationToken.None)).Should().BeNull("profiles are tenant-scoped");
    }

    [Fact]
    public async Task SaveAsync_ShouldRefuse_WhenAnotherUserOfTheTenantHoldsTheExtension()
    {
        var store = new InMemorySupervisorListenProfileStore();
        await store.SaveAsync(Profile(Tenant, "u1"), CancellationToken.None);

        (await store.SaveAsync(Profile(Tenant, "u2"), CancellationToken.None)).Should().BeFalse();
        (await store.GetAsync(Tenant, EntityId.From("u2"), CancellationToken.None)).Should().BeNull();
        (await store.SaveAsync(Profile(Other, "u2"), CancellationToken.None)).Should().BeTrue("the extension is unique per tenant only");
    }

    [Fact]
    public async Task SaveAsync_ShouldClearThePhone_WhenSetToNull()
    {
        var store = new InMemorySupervisorListenProfileStore();
        await store.SaveAsync(Profile(Tenant, "u1", extension: null, password: null, phone: "+573001234567"), CancellationToken.None);

        await store.SaveAsync(Profile(Tenant, "u1", extension: null, password: null, phone: null), CancellationToken.None);

        (await store.GetAsync(Tenant, EntityId.From("u1"), CancellationToken.None))!.PhoneNumber.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheProfile()
    {
        var store = new InMemorySupervisorListenProfileStore();
        await store.SaveAsync(Profile(Tenant, "u1"), CancellationToken.None);

        await store.DeleteAsync(Tenant, EntityId.From("u1"), CancellationToken.None);

        (await store.GetAsync(Tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeNull();
        (await store.SaveAsync(Profile(Tenant, "u2"), CancellationToken.None)).Should().BeTrue("the freed extension can be reused");
    }

    [Fact]
    public async Task ListWithCredentialsAsync_ShouldReturnOnlyTheTenantsProfilesWithCredentials()
    {
        var store = new InMemorySupervisorListenProfileStore();
        await store.SaveAsync(Profile(Tenant, "u1"), CancellationToken.None);
        await store.SaveAsync(Profile(Tenant, "u2", extension: null, password: null, phone: "+573001234567"), CancellationToken.None);
        await store.SaveAsync(Profile(Other, "u3", extension: "sl99999999"), CancellationToken.None);

        var list = await store.ListWithCredentialsAsync(Tenant, CancellationToken.None);

        list.Select(p => p.UserId.Value).Should().Equal("u1");
    }

    [Fact]
    public void SipPassword_ShouldBeJsonIgnored_OnTheEntity()
    {
        var property = typeof(SupervisorListenProfile).GetProperty(nameof(SupervisorListenProfile.SipPassword))!;

        property.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: false).Should().ContainSingle(
            "the secret crosses HTTP only through the owner's credentials route");
    }
}
