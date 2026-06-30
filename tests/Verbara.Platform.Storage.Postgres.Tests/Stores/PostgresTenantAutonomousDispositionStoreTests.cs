using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Typification;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed round-trip suite for <see cref="PostgresTenantAutonomousDispositionStore"/>
/// (ADR-0034). Reuses <see cref="MigrationsFixture"/> — it applies the REAL embedded migrations
/// (incl. 014, which creates <c>tenant_autonomous_disposition</c>) so the store is exercised against
/// the production DDL. Each test uses a unique tenant id so no per-test truncation is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTenantAutonomousDispositionStoreTests : IClassFixture<MigrationsFixture>
{
    private readonly PostgresTenantAutonomousDispositionStore _store;

    public PostgresTenantAutonomousDispositionStoreTests(MigrationsFixture fixture)
        => _store = new PostgresTenantAutonomousDispositionStore(fixture.DataSource);

    private static TenantAutonomousDisposition Record(TenantId tenant, string attestedBy = "user-1") => new()
    {
        TenantId = tenant,
        AttestedByUserId = attestedBy,
        AttestedAt = new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNoRecord()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");

        var result = await _store.GetAsync(tenant, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertThenGet_ShouldRoundTripActivation_WhenRecorded()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");

        await _store.UpsertAsync(Record(tenant), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.AttestedByUserId.Should().Be("user-1");
        loaded.AttestedAt.Should().Be(new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero));
        loaded.IsActive.Should().BeTrue();
        loaded.RevokedAt.Should().BeNull();
        loaded.RevokedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldReplace_WhenRecordExists()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        await _store.UpsertAsync(Record(tenant, "user-1"), CancellationToken.None);

        await _store.UpsertAsync(Record(tenant, "user-2"), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.AttestedByUserId.Should().Be("user-2");
    }

    [Fact]
    public async Task RevokeAsync_ShouldSoftDelete_WhenActive()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        await _store.UpsertAsync(Record(tenant), CancellationToken.None);

        await _store.RevokeAsync(tenant, EntityId.From("admin-9"), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        loaded.RevokedAt.Should().NotBeNull();
        loaded.RevokedByUserId.Should().Be("admin-9");
        loaded.AttestedByUserId.Should().Be("user-1");
    }

    [Fact]
    public async Task RevokeAsync_ShouldBeNoOp_WhenNoRecord()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");

        await _store.RevokeAsync(tenant, EntityId.From("admin-9"), CancellationToken.None);

        (await _store.GetAsync(tenant, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_ShouldPreserveFirstRevocation_WhenAlreadyRevoked()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        await _store.UpsertAsync(Record(tenant), CancellationToken.None);
        await _store.RevokeAsync(tenant, EntityId.From("admin-first"), CancellationToken.None);

        await _store.RevokeAsync(tenant, EntityId.From("admin-second"), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.RevokedByUserId.Should().Be("admin-first");
    }
}
