using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 1.4 (design D11, audit F2): the stale-cache save. The real Api host over a
/// Testcontainers Postgres (migrations applied at boot, the real <c>CachedTenantAuthConfigStore</c> with its 60 s TTL).
/// The settings read warms the cache with a null override; an operator then sets the override by SQL, which
/// invalidates nothing; the security save and, separately, the capacity save load the cached row (null override)
/// and save it whole. The column must still hold the SQL value after each: the whole-row save never names it.
/// </summary>
public sealed class InactivityOverrideStaleCacheLiveDbTests : IAsyncLifetime
{
    private const string Tenant = AuthenticatedPlatformApiFactory.TestTenantId;

    private IContainer? _postgres;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:18-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the entrypoint first runs a socket-only temporary server.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
        _connectionString = $"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
            "Database=postgres;Username=postgres;Password=postgres";
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task UpdateSettings_ShouldKeepTheSqlSetOverride_WhenTheCachedRowCarriesANullOverride()
    {
        using var factory = new AuthenticatedPlatformApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        await using var db = NpgsqlDataSource.Create(_connectionString);

        // The tenant's row exists (as after any earlier settings save), then a read warms the cache with it.
        using (var create = await client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new { auth = new { passwordMinLength = 13 } }))
            create.StatusCode.Should().Be(HttpStatusCode.OK, await create.Content.ReadAsStringAsync());
        using (var warm = await client.GetAsync("/api/v1/admin/tenant/settings"))
            warm.StatusCode.Should().Be(HttpStatusCode.OK);

        await ExecuteAsync(db, "UPDATE tenant_auth_config SET inactivity_close_after_hours = 48 WHERE tenant_id = @TenantId");

        using (var security = await client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new { auth = new { passwordMinLength = 14 } }))
            security.StatusCode.Should().Be(HttpStatusCode.OK, await security.Content.ReadAsStringAsync());
        (await OverrideAsync(db)).Should().Be(48, "the security save wrote the cached row whole, without the column");

        using (var warmAgain = await client.GetAsync("/api/v1/admin/tenant/settings"))
            warmAgain.StatusCode.Should().Be(HttpStatusCode.OK);
        await ExecuteAsync(db, "UPDATE tenant_auth_config SET inactivity_close_after_hours = 48 WHERE tenant_id = @TenantId");

        using (var capacity = await client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new { operational = new { maxChatDefault = 4 } }))
            capacity.StatusCode.Should().Be(HttpStatusCode.OK, await capacity.Content.ReadAsStringAsync());
        (await OverrideAsync(db)).Should().Be(48, "the capacity save wrote the cached row whole, without the column");
        (await PasswordMinLengthAsync(db)).Should().Be(14, "the saves themselves went through");
    }

    private static async Task ExecuteAsync(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant });
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<int?> OverrideAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand(
            "SELECT inactivity_close_after_hours FROM tenant_auth_config WHERE tenant_id = @TenantId");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant });
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull or null ? null : (int)value;
    }

    private static async Task<int> PasswordMinLengthAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("SELECT password_min_length FROM tenant_auth_config WHERE tenant_id = @TenantId");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant });
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
