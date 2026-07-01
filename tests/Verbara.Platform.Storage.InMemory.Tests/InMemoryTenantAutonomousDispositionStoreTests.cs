using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Typification;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTenantAutonomousDispositionStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static TenantAutonomousDisposition MakeRecord(string attestedBy = "user-1") => new()
    {
        TenantId = Tenant,
        AttestedByUserId = attestedBy,
        AttestedAt = new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNoRecord()
    {
        var store = new InMemoryTenantAutonomousDispositionStore();

        var result = await store.GetAsync(Tenant, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldPersistActivation_WhenRecorded()
    {
        var store = new InMemoryTenantAutonomousDispositionStore();

        await store.UpsertAsync(MakeRecord(), CancellationToken.None);

        var loaded = await store.GetAsync(Tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.AttestedByUserId.Should().Be("user-1");
        loaded.IsActive.Should().BeTrue();
        loaded.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldReplace_WhenRecordExists()
    {
        var store = new InMemoryTenantAutonomousDispositionStore();
        await store.UpsertAsync(MakeRecord("user-1"), CancellationToken.None);

        await store.UpsertAsync(MakeRecord("user-2"), CancellationToken.None);

        var loaded = await store.GetAsync(Tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.AttestedByUserId.Should().Be("user-2");
    }

    [Fact]
    public async Task RevokeAsync_ShouldSoftDelete_WhenActive()
    {
        var store = new InMemoryTenantAutonomousDispositionStore();
        await store.UpsertAsync(MakeRecord(), CancellationToken.None);
        var revoker = EntityId.From("admin-9");

        await store.RevokeAsync(Tenant, revoker, CancellationToken.None);

        var loaded = await store.GetAsync(Tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        loaded.RevokedAt.Should().NotBeNull();
        loaded.RevokedByUserId.Should().Be("admin-9");
        // The original attestation is preserved (append-only soft delete).
        loaded.AttestedByUserId.Should().Be("user-1");
    }

    [Fact]
    public async Task RevokeAsync_ShouldBeNoOp_WhenNoRecord()
    {
        var store = new InMemoryTenantAutonomousDispositionStore();

        await store.RevokeAsync(Tenant, EntityId.From("admin-9"), CancellationToken.None);

        (await store.GetAsync(Tenant, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_ShouldPreserveFirstRevocation_WhenAlreadyRevoked()
    {
        var store = new InMemoryTenantAutonomousDispositionStore();
        await store.UpsertAsync(MakeRecord(), CancellationToken.None);
        await store.RevokeAsync(Tenant, EntityId.From("admin-first"), CancellationToken.None);

        await store.RevokeAsync(Tenant, EntityId.From("admin-second"), CancellationToken.None);

        var loaded = await store.GetAsync(Tenant, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.RevokedByUserId.Should().Be("admin-first");
    }
}
