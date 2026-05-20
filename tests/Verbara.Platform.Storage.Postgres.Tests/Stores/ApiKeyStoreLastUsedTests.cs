using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Integration coverage for the Postgres-backed
/// <see cref="IApiKeyStore.UpdateLastUsedAsync"/> implementation that backs
/// R5.2 PC.5 / B.12. Validates round-trip persistence + read-back of the
/// new <c>last_used_at</c> column added by migration 020.
/// </summary>
[Collection("ApiKeyStoreLastUsed")]
public sealed class ApiKeyStoreLastUsedTests
{
    private readonly ApiKeyStoreLastUsedFixture _fixture;

    public ApiKeyStoreLastUsedTests(ApiKeyStoreLastUsedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpdateLastUsedAsync_ShouldPersistTimestamp_WhenCalled()
    {
        await _fixture.ResetAsync();

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = CreatePostgresStore(dataSource);
            var tenantId = new TenantId("tenant-x");
            var keyId = EntityId.New();
            var apiKey = new ApiKey
            {
                KeyId = keyId,
                TenantId = tenantId,
                Name = "test key",
                HashedKey = "deadbeef",
                Scopes = ["platform:*"],
                CreatedAt = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
                KeyType = ApiKeyType.Management,
            };

            await store.SaveAsync(apiKey, CancellationToken.None);

            // Sanity check — fresh row has no last-used yet.
            var fresh = await store.GetByIdAsync(tenantId, keyId, CancellationToken.None);
            fresh.Should().NotBeNull();
            fresh!.LastUsedAt.Should().BeNull();

            // Stamp + read-back round-trip.
            var stamp = new DateTimeOffset(2026, 4, 25, 13, 30, 15, TimeSpan.Zero);
            await store.UpdateLastUsedAsync(keyId, stamp, CancellationToken.None);

            var stamped = await store.GetByIdAsync(tenantId, keyId, CancellationToken.None);
            stamped.Should().NotBeNull();
            stamped!.LastUsedAt.Should().NotBeNull();
            // Postgres TIMESTAMPTZ has microsecond precision — compare with a tolerance.
            stamped.LastUsedAt!.Value.Should().BeCloseTo(stamp, TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public async Task UpdateLastUsedAsync_ShouldOverwritePreviousTimestamp_WhenCalledRepeatedly()
    {
        await _fixture.ResetAsync();

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = CreatePostgresStore(dataSource);
            var tenantId = new TenantId("tenant-y");
            var keyId = EntityId.New();
            await store.SaveAsync(
                new ApiKey
                {
                    KeyId = keyId,
                    TenantId = tenantId,
                    Name = "test key",
                    HashedKey = "cafef00d",
                    Scopes = ["platform:*"],
                    CreatedAt = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
                    KeyType = ApiKeyType.Management,
                },
                CancellationToken.None);

            var earlier = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
            var later = earlier.AddMinutes(5);

            await store.UpdateLastUsedAsync(keyId, earlier, CancellationToken.None);
            await store.UpdateLastUsedAsync(keyId, later, CancellationToken.None);

            var key = await store.GetByIdAsync(tenantId, keyId, CancellationToken.None);
            key!.LastUsedAt!.Value.Should().BeCloseTo(later, TimeSpan.FromMilliseconds(1));
        }
    }

    // Direct construction via InternalsVisibleTo (configured in
    // Verbara.Platform.Storage.Postgres.csproj) — no reflection needed.
    // Concrete return type satisfies CA1859 (avoid interface-typed return on a
    // private factory used solely within this fixture).
    private static PostgresApiKeyStore CreatePostgresStore(NpgsqlDataSource dataSource) =>
        new(dataSource);

    /// <summary>
    /// Direct DB read used by tests that want to bypass the store mapper.
    /// Kept as a helper for future last-used coverage that might check the
    /// raw column without round-tripping through the store's row mapper.
    /// </summary>
    [Fact]
    public async Task UpdateLastUsedAsync_ShouldWriteColumnDirectly_WhenInspectedViaSql()
    {
        await _fixture.ResetAsync();

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = CreatePostgresStore(dataSource);
            var tenantId = new TenantId("tenant-z");
            var keyId = EntityId.New();
            await store.SaveAsync(
                new ApiKey
                {
                    KeyId = keyId,
                    TenantId = tenantId,
                    Name = "raw-check",
                    HashedKey = "feedbabe",
                    Scopes = ["platform:*"],
                    CreatedAt = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
                    KeyType = ApiKeyType.Management,
                },
                CancellationToken.None);

            var stamp = new DateTimeOffset(2026, 4, 25, 14, 0, 0, TimeSpan.Zero);
            await store.UpdateLastUsedAsync(keyId, stamp, CancellationToken.None);

            await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync();
            // Npgsql maps TIMESTAMPTZ → DateTime (UTC kind) by default; we
            // convert at the test boundary rather than fight the type mapper.
            await using var cmd = new NpgsqlCommand(
                "SELECT last_used_at FROM api_keys WHERE key_id = @KeyId", conn);
            cmd.Parameters.Add(new NpgsqlParameter("KeyId", keyId.Value));
            var scalar = await cmd.ExecuteScalarAsync();
            var raw = scalar is DBNull or null ? (DateTime?)null : (DateTime)scalar;

            raw.Should().NotBeNull();
            new DateTimeOffset(raw!.Value, TimeSpan.Zero)
                .Should().BeCloseTo(stamp, TimeSpan.FromMilliseconds(1));
        }
    }
}
