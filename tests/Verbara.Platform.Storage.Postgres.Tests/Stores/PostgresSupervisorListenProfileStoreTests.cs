using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// supervisor-silent-listen 1.2 (design D3, D8, D12): <see cref="PostgresSupervisorListenProfileStore"/> against the
/// REAL embedded migrations (incl. <c>022_SupervisorListenProfiles.sql</c>). Each test uses a unique tenant id, so no
/// per-test truncation is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresSupervisorListenProfileStoreTests : IClassFixture<MigrationsFixture>
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private readonly MigrationsFixture _fixture;
    private readonly PostgresSupervisorListenProfileStore _store;
    private readonly TenantId _tenant = new($"t-{Guid.NewGuid():N}");
    private readonly TenantId _other = new($"t-{Guid.NewGuid():N}");

    public PostgresSupervisorListenProfileStoreTests(MigrationsFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresSupervisorListenProfileStore(fixture.DataSource);
    }

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
    public async Task Migration_ShouldCreateTheSupervisorListenProfilesTable()
    {
        (await _fixture.TableExistsAsync("supervisor_listen_profiles")).Should().BeTrue();
        (await _fixture.IndexExistsAsync("ux_supervisor_listen_profiles_extension")).Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNoProfile()
    {
        (await _store.GetAsync(_tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripAndUpsert_ByTenantAndUser()
    {
        (await _store.SaveAsync(Profile(_tenant, "u1"), CancellationToken.None)).Should().BeTrue();
        (await _store.SaveAsync(Profile(_tenant, "u1", phone: "+573001234567"), CancellationToken.None)).Should().BeTrue();

        var read = await _store.GetAsync(_tenant, EntityId.From("u1"), CancellationToken.None);
        read!.SipExtension.Should().Be("sl0a1b2c3d");
        read.SipPassword.Should().Be("pw");
        read.PhoneNumber.Should().Be("+573001234567");
        read.CreatedAt.Should().Be(At);
        (await _store.GetAsync(_other, EntityId.From("u1"), CancellationToken.None)).Should().BeNull("profiles are tenant-scoped");
    }

    [Fact]
    public async Task SaveAsync_ShouldRefuse_WhenAnotherUserOfTheTenantHoldsTheExtension()
    {
        await _store.SaveAsync(Profile(_tenant, "u1"), CancellationToken.None);

        (await _store.SaveAsync(Profile(_tenant, "u2"), CancellationToken.None)).Should().BeFalse();
        (await _store.GetAsync(_tenant, EntityId.From("u2"), CancellationToken.None)).Should().BeNull();
        (await _store.SaveAsync(Profile(_other, "u2"), CancellationToken.None)).Should().BeTrue("the extension is unique per tenant only");
    }

    [Fact]
    public async Task SaveAsync_ShouldClearThePhone_WhenSetToNull()
    {
        await _store.SaveAsync(Profile(_tenant, "u1", extension: null, password: null, phone: "+573001234567"), CancellationToken.None);

        await _store.SaveAsync(Profile(_tenant, "u1", extension: null, password: null, phone: null), CancellationToken.None);

        var read = await _store.GetAsync(_tenant, EntityId.From("u1"), CancellationToken.None);
        read!.PhoneNumber.Should().BeNull();
        read.SipExtension.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheProfile()
    {
        await _store.SaveAsync(Profile(_tenant, "u1"), CancellationToken.None);

        await _store.DeleteAsync(_tenant, EntityId.From("u1"), CancellationToken.None);

        (await _store.GetAsync(_tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeNull();
        (await _store.SaveAsync(Profile(_tenant, "u2"), CancellationToken.None)).Should().BeTrue("the freed extension can be reused");
    }

    [Fact]
    public async Task ListWithCredentialsAsync_ShouldReturnOnlyTheTenantsProfilesWithCredentials()
    {
        await _store.SaveAsync(Profile(_tenant, "u1"), CancellationToken.None);
        await _store.SaveAsync(Profile(_tenant, "u2", extension: null, password: null, phone: "+573001234567"), CancellationToken.None);
        await _store.SaveAsync(Profile(_other, "u3", extension: "sl99999999"), CancellationToken.None);

        var list = await _store.ListWithCredentialsAsync(_tenant, CancellationToken.None);

        list.Select(p => p.UserId.Value).Should().Equal("u1");
    }
}
