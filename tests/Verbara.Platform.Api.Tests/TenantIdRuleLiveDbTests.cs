using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// realtime-objects-stay-in-their-tenant (design D1, D2) over a real, migrated Postgres: two concurrent creates of
/// overlapping tenant ids through the Management route leave exactly one; first-run setup adopts a stored tenant whose
/// id is exactly the customer id; and the registry lists every stored id with its status.
/// </summary>
public sealed class TenantIdRuleLiveDbTests : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private const int Rounds = 20;
    private readonly ConversationClockReconcileLiveDbTests.PostgresContainer _postgres;

    public TenantIdRuleLiveDbTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres) => _postgres = postgres;

    private async Task<string> NewDatabaseAsync()
    {
        var name = $"tid_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(_postgres.ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();
        var connectionString = _postgres.ConnectionStringFor(name);
        DatabaseMigrationService.ApplyMigrations(connectionString);
        return connectionString;
    }

    [Fact]
    public async Task CreateTenant_ShouldLeaveExactlyOneOfTwoOverlappingIds_WhenBothArePostedAtOnce()
    {
        var connectionString = await NewDatabaseAsync();
        using var factory = new PlatformAdminApiFactory().WithWebHostBuilder(
            b => b.UseSetting("ConnectionStrings:Postgres", connectionString));
        using var client = PlatformAdminApiFactory.CreatePlatformAdminClient(factory);
        var store = factory.Services.GetRequiredService<ITenantStore>();

        for (var round = 1; round <= Rounds; round++)
        {
            var shorter = $"acme{round}x{Guid.NewGuid().ToString("N")[..6]}";
            var longer = shorter + "-corp";
            using var gate = new Barrier(2);
            var posts = new[] { shorter, longer }.Select(id => Task.Run(async () =>
            {
                gate.SignalAndWait();
                using var r = await client.PostAsJsonAsync("/api/management/tenants", new { tenantId = id, name = id, type = 2 });
                return r.StatusCode;
            })).ToArray();
            var statuses = await Task.WhenAll(posts);

            statuses.Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict], $"round {round}");
            var exist = 0;
            foreach (var id in new[] { shorter, longer })
                exist += await store.GetAsync(id) is null ? 0 : 1;
            exist.Should().Be(1, $"round {round}: exactly one of the two tenants exists");
        }
    }

    /// <summary>
    /// First-run setup adopts a stored tenant whose id is exactly the customer id by writing it under the host with the
    /// tenant store's upsert (design D1, "First-run setup"). On Postgres that write must land the parent and the type,
    /// as the in-memory store does. Driven at the store: setup's own route signs a token, which this host cannot do over
    /// a fresh database, and the route is covered by <c>SetupEndpointTests</c>.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_ShouldAdoptAStoredOrphan_WhenSetupWritesItsExactIdUnderTheHostOnPostgres()
    {
        var connectionString = await NewDatabaseAsync();
        using var factory = new PlatformAdminApiFactory().WithWebHostBuilder(
            b => b.UseSetting("ConnectionStrings:Postgres", connectionString));
        var store = factory.Services.GetRequiredService<ITenantStore>();
        await store.UpsertAsync(new Tenant
        {
            TenantId = "acme", Name = "acme", Status = TenantStatus.Active, Type = TenantType.Partner, ParentTenantId = null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await store.UpsertAsync(new Tenant
        {
            TenantId = "acme", Name = "Acme", Status = TenantStatus.Active, Type = TenantType.Customer,
            ParentTenantId = PlatformAdminApiFactory.HostTenantId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        var adopted = await store.GetAsync("acme");
        adopted!.ParentTenantId.Should().Be(PlatformAdminApiFactory.HostTenantId, "the stored tenant is adopted by the host");
        adopted.Type.Should().Be(TenantType.Customer);
    }

    [Fact]
    public async Task ListAllIdsAsync_ShouldReturnEveryStoredIdWithItsStatus_WhenTenantsOfEveryStatusAreStored()
    {
        var connectionString = await NewDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var statuses = Enum.GetValues<TenantStatus>();
        foreach (var status in statuses)
        {
            await using var cmd = dataSource.CreateCommand(
                "INSERT INTO tenants (tenant_id, name, status, type, created_at, updated_at) VALUES (@Id, @Id, @Status, 2, now(), now())");
            cmd.Parameters.Add(new NpgsqlParameter("Id", "s-" + status.ToString().ToLowerInvariant()));
            cmd.Parameters.Add(new NpgsqlParameter("Status", (int)status));
            await cmd.ExecuteNonQueryAsync();
        }
        var registry = new PostgresTenantIdRegistry(dataSource);

        var all = await registry.ListAllIdsAsync(default);

        all.Should().BeEquivalentTo(statuses.Select(s => new StoredTenantId("s-" + s.ToString().ToLowerInvariant(), s)));
    }

    [Fact]
    public async Task DeleteTenant_ShouldLeaveThePairFlaggedAndUnreconciled_WhenOneMemberIsDeleted()
    {
        var connectionString = await NewDatabaseAsync();
        using var factory = new PlatformAdminApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", connectionString);
            b.UseSetting("Realtime:TenantIds:0", "acme-corp");
        });
        using var client = PlatformAdminApiFactory.CreatePlatformAdminClient(factory);
        await using (var db = NpgsqlDataSource.Create(connectionString))
        {
            foreach (var id in new[] { "acme", "acme-corp" })
            {
                await using var cmd = db.CreateCommand(
                    "INSERT INTO tenants (tenant_id, name, status, type, parent_tenant_id, created_at, updated_at) " +
                    "VALUES (@Id, @Id, 0, 2, 'platform', now(), now())");
                cmd.Parameters.Add(new NpgsqlParameter("Id", id));
                await cmd.ExecuteNonQueryAsync();
            }
        }

        using var deleted = await client.DeleteAsync(new Uri("/api/management/tenants/acme", UriKind.Relative));
        using var ready = await factory.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        var body = await ready.Content.ReadAsStringAsync();
        ready.StatusCode.Should().Be(HttpStatusCode.OK, body);
        System.Text.Json.Nodes.JsonNode.Parse(body)!["checks"]!["tenant-ids"]?["status"]?.GetValue<string>()
            .Should().Be("Degraded", body);
        body.Should().NotContain("acme", "readiness carries counts only; the ids are in the Api log");
        (await factory.Services.GetRequiredService<Verbara.Sdk.Pro.Realtime.Engine.IDesiredStateProvider>()
            .GetActiveTenantIdsAsync()).Should().BeEmpty("deleting one member of a pair unflags nothing");
    }
}
