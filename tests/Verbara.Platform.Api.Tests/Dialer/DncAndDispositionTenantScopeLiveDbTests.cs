using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Verbara.Sdk.Pro.Dialer;
using Verbara.Sdk.Pro.Dialer.Compliance;
using static Verbara.Platform.Api.Tests.Dialer.DncAndDispositionTenantScopeTests;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dnc-and-dispositions-are-tenant-scoped, against Pro's Postgres stores: the real Api <c>Program</c> with the dialer
/// registered over a Testcontainers Postgres (<see cref="DialerTenantScopeFactory"/>). The DNC entry routes and
/// the disposition route answer 404 for a list or a call attempt of another tenant and change nothing; the tenant's
/// own list and attempt work; and the host tenant is the platform DNC tenant the dialer's checker reads.
/// </summary>
public sealed class DncAndDispositionTenantScopeLiveDbTests : IClassFixture<DialerTenantScopeLiveDbFixture>
{
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeLiveDbFixture _fixture;

    public DncAndDispositionTenantScopeLiveDbTests(DialerTenantScopeLiveDbFixture fixture)
    {
        _fixture = fixture;
    }

    private DialerTenantScopeFactory Factory => _fixture.Factory;

    private string ConnectionString() => _fixture.ConnectionString;

    [Theory]
    [MemberData(nameof(EntryRoutes), MemberType = typeof(DncAndDispositionTenantScopeTests))]
    public async Task DncEntryRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListBelongsToAnotherTenant(string route)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var listId = await CreateListWithEntryAsync(acme);

        using var response = await SendEntryRouteAsync(globex, route, listId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "list {0} belongs to acme, so it is no list of globex's", listId);
        (await EntriesAsync(acme, listId)).Should().Equal(["573001234567"],
            "a route of another tenant neither adds, removes nor imports an entry");
    }

    [Theory]
    [MemberData(nameof(EntryRoutes), MemberType = typeof(DncAndDispositionTenantScopeTests))]
    public async Task DncEntryRoute_ShouldSucceed_WhenTheListBelongsToTheTenant(string route)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var listId = await CreateListWithEntryAsync(acme);

        using var response = await SendEntryRouteAsync(acme, route, listId);

        response.IsSuccessStatusCode.Should().BeTrue("acme owns list {0}", listId);
    }

    [Fact]
    public async Task CreateDncList_ShouldReturn403AndCreateNothing_WhenATenantAdministratorAsksForGlobalScope()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var name = $"dnc-{Guid.NewGuid():N}";

        using var response = await acme.PostAsync("/api/admin/dnc-lists", JsonContent.Create(new { name, scope = "Global" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ListNamesAsync(acme)).Should().NotContain(name);
    }

    [Fact]
    public async Task UpdateCallAttemptDisposition_ShouldReturn404AndChangeNothing_WhenTheAttemptBelongsToAnotherTenant()
    {
        var attemptId = await SeedCallAttemptAsync(Acme);
        using var globex = Factory.CreateAdminClient(Globex);

        using var response = await globex.PutAsync($"/api/admin/call-attempts/{attemptId}/disposition",
            JsonContent.Create(new { dispositionId = 5, agentComment = "not mine" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var (dispositionId, comment) = await DispositionAsync(attemptId);
        dispositionId.Should().BeNull();
        comment.Should().BeNull();
    }

    [Fact]
    public async Task UpdateCallAttemptDisposition_ShouldReturn204AndStoreIt_WhenTheAttemptBelongsToTheTenant()
    {
        var attemptId = await SeedCallAttemptAsync(Acme);
        using var acme = Factory.CreateAdminClient(Acme);

        using var response = await acme.PutAsync($"/api/admin/call-attempts/{attemptId}/disposition",
            JsonContent.Create(new { dispositionId = 5, agentComment = "callback tomorrow" }));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (dispositionId, comment) = await DispositionAsync(attemptId);
        dispositionId.Should().Be(5);
        comment.Should().Be("callback tomorrow");
    }

    [Fact]
    public async Task DncChecker_ShouldApplyOnlyTheHostTenantsGlobalLists_WhenTheHostTenantExists()
    {
        await SeedGlobalListAsync(DialerTenantScopeFactory.HostTenantId, "573001110000");
        await SeedGlobalListAsync(Globex, "573002220000");

        var options = Factory.Services.GetRequiredService<IOptions<DialerOptions>>().Value;
        var checker = Factory.Services.GetRequiredService<DncCheckerBase>();

        options.PlatformDncTenantIdResolver.Should().NotBeNull("the host names its host tenant as the platform DNC tenant");
        (await options.PlatformDncTenantIdResolver!(CancellationToken.None))
            .Should().Be(DialerTenantScopeFactory.HostTenantId);
        (await checker.IsBlockedAsync(Acme, "573001110000", dncListId: null, checkGlobal: true, CancellationToken.None))
            .Should().BeTrue("the host tenant's Global list applies to every tenant's campaigns");
        (await checker.IsBlockedAsync(Acme, "573002220000", dncListId: null, checkGlobal: true, CancellationToken.None))
            .Should().BeFalse("a Global list of a tenant that is not the host blocks only that tenant's campaigns");
    }

    [Fact]
    public async Task DncChecker_ShouldApplyNoOtherTenantsList_WhenSetupHasNotCreatedTheHostTenant()
    {
        using var factory = new DialerTenantScopeFactory(ConnectionString(), seedHostTenant: false);
        await SeedGlobalListAsync(DialerTenantScopeFactory.HostTenantId, "573003330000");

        var options = factory.Services.GetRequiredService<IOptions<DialerOptions>>().Value;
        var checker = factory.Services.GetRequiredService<DncCheckerBase>();

        options.PlatformDncTenantIdResolver.Should().NotBeNull("the host names its host tenant as the platform DNC tenant");
        (await options.PlatformDncTenantIdResolver!(CancellationToken.None)).Should().BeNull();
        (await checker.IsBlockedAsync(Acme, "573003330000", dncListId: null, checkGlobal: true, CancellationToken.None))
            .Should().BeFalse("before setup no tenant is the platform DNC tenant");
    }

    // ─── Postgres seeding ───────────────────────────────────────────────────────

    /// <summary>One dialed contact of a campaign of <paramref name="tenantId"/>, and its call attempt.</summary>
    private async Task<long> SeedCallAttemptAsync(string tenantId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            WITH c AS (INSERT INTO campaigns (tenant_id, name) VALUES (@T, 'scope') RETURNING id),
                 l AS (INSERT INTO contact_lists (tenant_id, campaign_id, name) SELECT @T, c.id, 'scope' FROM c RETURNING id, campaign_id),
                 k AS (INSERT INTO dialer_contacts (tenant_id, contact_list_id) SELECT @T, l.id FROM l RETURNING id, contact_list_id),
                 p AS (INSERT INTO contact_phones (contact_id, phone_number) SELECT k.id, '573004440000' FROM k RETURNING id, contact_id)
            INSERT INTO call_attempts (tenant_id, campaign_id, contact_id, contact_list_id, contact_phone_id, phone_number)
            SELECT @T, l.campaign_id, k.id, k.contact_list_id, p.id, '573004440000' FROM l, k, p
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("T", tenantId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(long? DispositionId, string? Comment)> DispositionAsync(long attemptId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT disposition_id, agent_comment FROM call_attempts WHERE id = @Id", conn);
        cmd.Parameters.AddWithValue("Id", attemptId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>A Global-scope list of <paramref name="tenantId"/> holding <paramref name="phone"/>.</summary>
    private async Task SeedGlobalListAsync(string tenantId, string phone)
    {
        await using var conn = new NpgsqlConnection(ConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            WITH l AS (INSERT INTO dnc_lists (tenant_id, name, scope) VALUES (@T, 'global', 1) RETURNING id)
            INSERT INTO dnc_entries (dnc_list_id, phone_number) SELECT l.id, @P FROM l
            """, conn);
        cmd.Parameters.AddWithValue("T", tenantId);
        cmd.Parameters.AddWithValue("P", phone);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>One Testcontainers Postgres and one host over it, shared by <see cref="DncAndDispositionTenantScopeLiveDbTests"/>.</summary>
public sealed class DialerTenantScopeLiveDbFixture : IAsyncLifetime, IDisposable
{
    private IContainer? _postgres;
    private DialerTenantScopeFactory? _factory;

    /// <summary>The host, with the host tenant seeded (as after <c>/api/setup</c>).</summary>
    public DialerTenantScopeFactory Factory => _factory!;

    /// <summary>The container's connection string.</summary>
    public string ConnectionString =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres;Application Name=dialer-tenant-scope";

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP (see DialerHostWiringTests).
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
        _factory = new DialerTenantScopeFactory(ConnectionString);
        _ = _factory.Services; // boots Program.cs, which runs the dialer migrations
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    /// <inheritdoc />
    public void Dispose() => _factory?.Dispose();
}
