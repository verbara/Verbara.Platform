using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Events;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Tests.Realtime;

/// <summary>
/// realtime-objects-stay-in-their-tenant (task 1.4) over a real, migrated Postgres with Pro's realtime schema and
/// Pro's real <see cref="IRealtimeSyncService"/>: the suspensions (Partner route and dunning), the deletion (Management
/// <c>DELETE</c>), the queue deletion (admin queue route) and one cycle of Pro's real reconciler fed by Platform's real
/// <see cref="Verbara.Sdk.Pro.Realtime.Engine.IDesiredStateProvider"/>. Each test reads the realtime tables themselves,
/// before and after, so what it asserts is the rows Asterisk would read.
/// </summary>
/// <remarks>
/// Tenants that break the tenant-id rule (<c>acme</c> with <c>acme-corp</c>, <c>t</c>, <c>acm_</c>) are seeded straight
/// into the tenant store, as a deployment from before the rule holds them. Agent and user ids are 32-hex strings. The
/// class uses only types that exist before the change, so it runs unchanged against the unfixed code.
/// </remarks>
public sealed class RealtimeTenantIsolationLiveDbTests : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private const string North = "north";
    private const string South = "south";

    private readonly ConversationClockReconcileLiveDbTests.PostgresContainer _postgres;

    public RealtimeTenantIsolationLiveDbTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres) => _postgres = postgres;

    private async Task<string> NewDatabaseAsync()
    {
        var name = $"rti_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(_postgres.ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();
        var connectionString = _postgres.ConnectionStringFor(name);
        DatabaseMigrationService.ApplyMigrations(connectionString);
        return connectionString;
    }

    private async Task<RealtimeLiveDbApiFactory> NewHostAsync(params string[] realtimeTenantIds)
    {
        var factory = new RealtimeLiveDbApiFactory(await NewDatabaseAsync(), North, realtimeTenantIds);
        _ = factory.Server;
        await factory.SeedTenantAsync(North, TenantType.Partner, RealtimeLiveDbApiFactory.HostTenantId);
        await factory.SeedTenantAsync(South, TenantType.Partner, RealtimeLiveDbApiFactory.HostTenantId);
        return factory;
    }

    private static string Hex() => Guid.NewGuid().ToString("N");

    // ─── Suspending or deleting a tenant removes no other tenant's realtime rows ──────────────────────────────────

    [Fact]
    public async Task SuspendCustomer_ShouldLeaveTheTenantWhoseIdItPrefixesIntact_WhenItsPartnerSuspendsIt()
    {
        using var host = await NewHostAsync();
        var (a1, b1) = (Hex(), Hex());
        await host.SeedTenantAsync("acme", TenantType.Customer, North);
        await host.SeedTenantAsync("acme-corp", TenantType.Customer, South);
        var acme = await host.SeedRealtimeAsync("acme", agents: [a1], queues: ["support"]);
        var corp = await host.SeedRealtimeAsync("acme-corp", agents: [b1], queues: ["sales"],
            members: [("sales", b1)], trunks: 1);

        await host.PartnerSuspendAsync("acme");

        var rows = await host.RowsAsync();
        rows.Should().Contain(corp.All, "acme-corp's realtime rows are not acme's");
        rows.Should().NotContain(acme.AgentRows(a1), "acme's own agent endpoint, auth and AOR are removed");
    }

    [Fact]
    public async Task SuspendCustomer_ShouldLeaveEveryOtherTenantsTrunks_WhenTheCustomerIsNamedT()
    {
        using var host = await NewHostAsync();
        await host.SeedTenantAsync("t", TenantType.Customer, North);
        await host.SeedTenantAsync("globex", TenantType.Customer, South);
        var globex = await host.SeedRealtimeAsync("globex", trunks: 1, trunkMatchHost: "198.51.100.7");
        globex.Trunks.Should().Contain(r => r.StartsWith("ps_endpoint_id_ips:ipauth-t-", StringComparison.Ordinal));

        await host.PartnerSuspendAsync("t");

        (await host.RowsAsync()).Should().Contain(globex.Trunks, "a tenant named t owns no trunk row t-{n}");
    }

    [Fact]
    public async Task SuspendCustomer_ShouldLeaveOtherTenantsRows_WhenTheCustomerIdHoldsAWildcard()
    {
        using var host = await NewHostAsync();
        var b1 = Hex();
        await host.SeedTenantAsync("acm_", TenantType.Customer, North);
        await host.SeedTenantAsync("acme", TenantType.Customer, South);
        var acme = await host.SeedRealtimeAsync("acme", agents: [b1], queues: ["support"], members: [("support", b1)]);

        await host.PartnerSuspendAsync("acm_");

        (await host.RowsAsync()).Should().Contain(acme.All, "acm_ is a literal id, not a pattern");
    }

    [Fact]
    public async Task SuspendCustomer_ShouldRemoveItsOwnRowsAndItsTrunksCredentials_WhenTheTenantIsNotFlagged()
    {
        using var host = await NewHostAsync();
        var (g1, a1) = (Hex(), Hex());
        await host.SeedTenantAsync("globex", TenantType.Customer, North);
        await host.SeedTenantAsync("acme", TenantType.Customer, South);
        var globex = await host.SeedRealtimeAsync("globex", agents: [g1], queues: ["support"], trunks: 1);
        var acme = await host.SeedRealtimeAsync("acme", agents: [a1], queues: ["support"]);

        await host.PartnerSuspendAsync("globex");

        var rows = await host.RowsAsync();
        rows.Should().NotContain(globex.AgentRows(g1));
        rows.Should().NotContain("queues:globex-support");
        rows.Should().NotContain(globex.Trunks, "the trunk's endpoint, credentials and registration go with its tenant");
        rows.Should().Contain(acme.All);
    }

    [Fact]
    public async Task DunningEscalation_ShouldSuspendTheTenantAndLeaveItsPairPartnersRows_WhenItsDunningReachesSuspension()
    {
        using var host = await NewHostAsync();
        var (a1, b1) = (Hex(), Hex());
        await host.SeedTenantAsync("acme", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId, TenantStatus.Warning);
        await host.SeedTenantAsync("acme-corp", TenantType.Customer, South);
        var acme = await host.SeedRealtimeAsync("acme", agents: [a1]);
        var corp = await host.SeedRealtimeAsync("acme-corp", agents: [b1], queues: ["sales"],
            members: [("sales", b1)], trunks: 1);
        var config = new DunningConfig();
        var startedAt = DateTimeOffset.UtcNow.AddDays(-(config.SuspendedDays + config.PendingDeletionDays) / 2.0);
        using (var scope = host.Services.CreateScope())
        {
            var invoiceId = EntityId.New();
            await scope.ServiceProvider.GetRequiredService<IInvoiceStore>().SaveAsync(new Invoice
            {
                InvoiceId = invoiceId, TenantId = new TenantId("acme"), PeriodStart = startedAt.AddDays(-30),
                PeriodEnd = startedAt.AddDays(-14), Currency = "USD", LineItems = [], Subtotal = 10m, Total = 10m,
                Status = InvoiceStatus.Issued, GeneratedAt = startedAt.AddDays(-14), IssuedAt = startedAt.AddDays(-14),
                PaymentStatus = PaymentStatus.Overdue, DueDate = startedAt,
            }, default);
            await scope.ServiceProvider.GetRequiredService<IDunningStore>().UpsertAsync(new DunningRecord
            {
                DunningId = EntityId.New().Value, TenantId = "acme", InvoiceId = invoiceId.Value,
                CurrentStage = TenantStatus.Warning, StartedAt = startedAt,
            }, default);
        }
        var suspended = host.Lifecycle.WaitForSuspended("acme");
        var dunning = new DunningService(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<ILogger<DunningService>>(), Microsoft.Extensions.Options.Options.Create(config));

        await dunning.StartAsync(default);
        await suspended.WaitAsync(TimeSpan.FromSeconds(60)); // every handler of acme's suspension has returned
        await dunning.StopAsync(default);

        (await host.Services.GetRequiredService<ITenantStore>().GetAsync("acme"))!.Status.Should().Be(TenantStatus.Suspended);
        var rows = await host.RowsAsync();
        rows.Should().NotContain(acme.AgentRows(a1), "acme's own agent rows are removed by its suspension");
        rows.Should().Contain(corp.All, "acme-corp's realtime rows are not acme's");
    }

    // ─── A flagged tenant is not reconciled: a configured tenant that is not stored is left out ───────────────────

    [Fact]
    public async Task Reconcile_ShouldLeaveAStoredTenantsQueueIntact_WhenTheDefaultRealtimeTenantIsNotStored()
    {
        using var host = await NewHostAsync();
        await host.SeedTenantAsync("demo-corp", TenantType.Customer, North);
        await host.SeedRealtimeAsync("demo-corp", queues: ["sales"]);
        (await host.RowsAsync()).Should().Contain("queues:demo-corp-sales");

        await host.RunOneReconcileCycleAsync();

        (await host.RowsAsync()).Should().Contain("queues:demo-corp-sales", "demo is configured but not stored");
        host.Sync.Attempts.Should().NotContain(call => call.StartsWith("RemoveQueue:demo:", StringComparison.Ordinal));
    }

    // ─── A flagged tenant's suspension or deletion removes its rows by exact id and no queue row ──────────────────

    [Fact]
    public async Task SuspendTenant_ShouldRemoveAFlaggedTenantsRowsByExactIdAndKeepEveryQueue_WhenAPairMemberIsSuspended()
    {
        using var host = await NewHostAsync();
        var (a1, b1, u1) = (Hex(), Hex(), Hex());
        await host.SeedTenantAsync("acme", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        await host.SeedTenantAsync("acme-corp", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        var acme = await host.SeedRealtimeAsync("acme", agents: [a1], queues: ["corp-x"], members: [("corp-x", a1)],
            listenUsers: [u1], trunks: 1);
        var corp = await host.SeedRealtimeAsync("acme-corp", agents: [b1], queues: ["x"], members: [("x", b1)]);

        using var response = await host.ManagementClient().PostAsync(
            new Uri("/api/management/tenants/acme/suspend", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = await host.RowsAsync();
        rows.Should().NotContain(acme.AgentRows(a1));
        rows.Should().NotContain(acme.AgentRows("sup-" + u1).Where(r => !r.StartsWith("ps_aors:", StringComparison.Ordinal)));
        rows.Should().NotContain($"queue_members:acme-corp-x|PJSIP/acme-agent-{a1}");
        rows.Should().NotContain(acme.Trunks);
        rows.Should().Contain("queues:acme-corp-x", "a flagged tenant's queue row is never removed");
        rows.Should().Contain(corp.All);
        host.WarningsNaming("acme-corp-x").Should().NotBeEmpty("each kept queue is logged");
    }

    [Fact]
    public async Task DeleteTenant_ShouldRemoveAFlaggedTenantsSlotsAndTrunksAndKeepEveryQueue_WhenAPairMemberIsDeleted()
    {
        using var host = await NewHostAsync();
        var (b1, u1) = (Hex(), Hex());
        await host.SeedTenantAsync("acme", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        await host.SeedTenantAsync("acme-corp", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        var acme = await host.SeedRealtimeAsync("acme", queues: ["support", "corp-x"], listenUsers: [u1], trunks: 1);
        var corp = await host.SeedRealtimeAsync("acme-corp", agents: [b1], queues: ["x"], members: [("x", b1)]);

        using var response = await host.ManagementClient().DeleteAsync(new Uri("/api/management/tenants/acme", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var rows = await host.RowsAsync();
        rows.Should().NotContain($"ps_endpoints:acme-agent-sup-{u1}");
        rows.Should().NotContain($"ps_auths:auth-acme-agent-sup-{u1}");
        rows.Should().NotContain(acme.Trunks);
        rows.Should().Contain(["queues:acme-support", "queues:acme-corp-x"], "a flagged tenant's queue rows are never removed");
        rows.Should().Contain(corp.All);
        host.WarningsNaming("acme-support").Should().NotBeEmpty();
        host.WarningsNaming("acme-corp-x").Should().NotBeEmpty();
        host.Sync.Attempts.Should().NotContain(call => call.StartsWith("CleanupTenant:", StringComparison.Ordinal),
            "Pro's lifecycle handler (the only caller of CleanupTenantAsync) is not reached");
    }

    [Fact]
    public async Task SuspendTenant_ShouldRemoveAMembershipBeyondTheFirstPageOfQueues_WhenTheAgentHasNoSipCredentials()
    {
        using var host = await NewHostAsync();
        var a2 = Hex();
        await host.SeedTenantAsync("acme", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        await host.SeedTenantAsync("acme-corp", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        await host.InsertAgentAsync("acme", a2, extension: null, sipPassword: null);
        using (var scope = host.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var queues = sp.GetRequiredKeyedService<IQueueStore>(RealtimeSyncingStoresExtensions.QueueStoreInner);
            for (var i = 1; i <= 1001; i++)
                await queues.SaveAsync(RealtimeLiveDbApiFactory.Queue("acme", $"q{i:D4}", id: $"id{i:D4}"), default);
            await sp.GetRequiredService<IQueueMembershipStore>().SaveAsync(new QueueMembership
            {
                TenantId = new TenantId("acme"), QueueId = EntityId.From("id1001"), AgentId = EntityId.From(a2),
                Source = MembershipSource.Manual, CreatedAt = DateTimeOffset.UtcNow,
            }, default);
        }
        await host.Sync.AddQueueMemberAsync("acme", "q1001", a2, a2);
        (await host.RowsAsync()).Should().Contain($"queue_members:acme-q1001|PJSIP/acme-agent-{a2}");

        using var response = await host.ManagementClient().PostAsync(
            new Uri("/api/management/tenants/acme/suspend", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await host.RowsAsync()).Should().NotContain($"queue_members:acme-q1001|PJSIP/acme-agent-{a2}");
    }

    // ─── Deleting a queue of a flagged tenant keeps the realtime queue row ────────────────────────────────────────

    [Fact]
    public async Task DeleteQueue_ShouldKeepThePairPartnersQueueRow_WhenAFlaggedTenantDeletesItsQueue()
    {
        using var host = await NewHostAsync();
        var b1 = Hex();
        await host.SeedTenantAsync("acme", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        await host.SeedTenantAsync("acme-corp", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);
        var corp = await host.SeedRealtimeAsync("acme-corp", agents: [b1], queues: ["x"], members: [("x", b1)]);

        await host.CreateAndDeleteQueueAsync("acme", "corp-x");

        (await host.Services.GetRequiredService<IQueueStore>().GetByNameAsync(new TenantId("acme"), "corp-x", default))
            .Should().BeNull("the Platform record is deleted all the same");
        (await host.RowsAsync()).Should().Contain(corp.All, "acme's corp-x is acme-corp's x row");
        host.WarningsNaming("acme-corp-x").Should().NotBeEmpty("the kept name is logged");
    }

    [Fact]
    public async Task DeleteQueue_ShouldRemoveTheRealtimeQueueRow_WhenTheTenantIsNotFlagged()
    {
        using var host = await NewHostAsync();
        await host.SeedTenantAsync("globex", TenantType.Customer, RealtimeLiveDbApiFactory.HostTenantId);

        await host.CreateAndDeleteQueueAsync("globex", "support");

        (await host.RowsAsync()).Should().NotContain("queues:globex-support");
    }
}

/// <summary>The realtime rows one seed wrote, as <c>table:id</c> (queue members as <c>queue_members:queue|interface</c>).</summary>
internal sealed record SeededRealtimeRows(string TenantId, IReadOnlyList<string> All, IReadOnlyList<string> Trunks)
{
    /// <summary>The endpoint, auth and AOR rows of <paramref name="agentId"/> of this tenant.</summary>
    public IReadOnlyList<string> AgentRows(string agentId) =>
    [
        $"ps_endpoints:{TenantId}-agent-{agentId}", $"ps_auths:auth-{TenantId}-agent-{agentId}", $"ps_aors:{TenantId}-agent-{agentId}",
    ];
}

/// <summary>
/// A host over a real Postgres database whose realtime seam is Pro's real engine (recorded by
/// <see cref="RecordingRealtimeSync"/>) and whose trunk store is the production one (Postgres, synced to realtime).
/// Auth: a Partner admin key for <c>partnerTenantId</c>, a Management key, and a tenant admin key per tenant on request.
/// </summary>
internal sealed class RealtimeLiveDbApiFactory : WebApplicationFactory<Program>
{
    public const string HostTenantId = "platform";
    private const string PartnerKey = "rt-live-partner-key";
    private const string ManagementKey = "rt-live-management-key";
    private const string EngineTypeName = "Verbara.Sdk.Pro.Realtime.Engine.RealtimeSyncEngine";
    private const string ReconcilerTypeName = "Verbara.Sdk.Pro.Realtime.Engine.RealtimeReconciler";

    /// <summary>Every row of the realtime tables, as <c>table:id</c> (queue members as <c>queue_members:queue|interface</c>).</summary>
    private const string RowsSql = """
        SELECT 'ps_endpoints:' || id FROM ps_endpoints
        UNION ALL SELECT 'ps_auths:' || id FROM ps_auths
        UNION ALL SELECT 'ps_aors:' || id FROM ps_aors
        UNION ALL SELECT 'ps_registrations:' || id FROM ps_registrations
        UNION ALL SELECT 'ps_endpoint_id_ips:' || id FROM ps_endpoint_id_ips
        UNION ALL SELECT 'queues:' || name FROM queues
        UNION ALL SELECT 'queue_members:' || queue_name || '|' || interface FROM queue_members
        """;

    private readonly string _connectionString;
    private readonly string _partnerTenantId;
    private readonly string[] _realtimeTenantIds;
    private readonly ConcurrentDictionary<string, ApiKey> _keys = new(StringComparer.Ordinal);
    private readonly ReconcileCycleSignal _cycle = new();
    private IUserStore? _users;
    private Type? _reconcilerType;

    public RealtimeLiveDbApiFactory(string connectionString, string partnerTenantId, string[] realtimeTenantIds)
    {
        _connectionString = connectionString;
        _partnerTenantId = partnerTenantId;
        _realtimeTenantIds = realtimeTenantIds;
    }

    public LogRecordCapture Logs { get; } = new();

    public RecordingRealtimeSync Sync => Services.GetRequiredService<RecordingRealtimeSync>();

    public SignallingLifecycleDispatcher Lifecycle => Services.GetRequiredService<SignallingLifecycleDispatcher>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        for (var i = 0; i < _realtimeTenantIds.Length; i++)
            builder.UseSetting($"Realtime:TenantIds:{i}", _realtimeTenantIds[i]);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            logging.AddProvider(_cycle);
            // The reconciler writes its end-of-cycle line at Debug; the test waits on that line (a causal signal).
            logging.AddFilter(ReconcilerTypeName, LogLevel.Debug);
        });

        builder.ConfigureServices(services =>
        {
            // The realtime seams the in-memory helpers below replace with no-op substitutes: kept as Program.cs
            // registered them (Pro's engine, Pro's Postgres profile store, the realtime-syncing Postgres trunk store).
            var kept = services.Where(d =>
                d.ServiceType == typeof(IRealtimeSyncService) || d.ServiceType.FullName == EngineTypeName
                || d.ServiceType == typeof(EndpointProfileStoreBase) || d.ServiceType == typeof(TrunkStoreBase)).ToList();
            var engineType = kept.Single(d => d.ServiceType.FullName == EngineTypeName).ServiceType;
            _reconcilerType = services.Single(d => d.ServiceType.FullName == ReconcilerTypeName).ServiceType;

            AuthenticatedPlatformApiFactory.SetupTestAuth(services, Hash(PartnerKey), _partnerTenantId, "partner-admin");
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            foreach (var d in services.Where(d => d.ServiceType == typeof(IRealtimeSyncService)
                         || d.ServiceType == typeof(EndpointProfileStoreBase) || d.ServiceType == typeof(TrunkStoreBase)).ToList())
                services.Remove(d);
            foreach (var d in kept.Where(d => d.ServiceType != typeof(IRealtimeSyncService)))
                services.Add(d);
            services.AddSingleton(sp => new RecordingRealtimeSync((IRealtimeSyncService)sp.GetRequiredService(engineType)));
            services.AddSingleton<IRealtimeSyncService>(sp => sp.GetRequiredService<RecordingRealtimeSync>());

            // Platform's real dispatcher, wrapped so a test can wait until every handler of a lifecycle event returned.
            var dispatcher = services.Single(d => d.ServiceType == typeof(ITenantLifecycleDispatcher));
            services.Remove(dispatcher);
            services.AddSingleton(sp => new SignallingLifecycleDispatcher(
                (ITenantLifecycleDispatcher)ActivatorUtilities.CreateInstance(sp, dispatcher.ImplementationType!)));
            services.AddSingleton<ITenantLifecycleDispatcher>(sp => sp.GetRequiredService<SignallingLifecycleDispatcher>());

            // One lookup over every key this host hands out; the Partner key is SetupTestAuth's own.
            var apiKeys = (IApiKeyStore)services.Last(d => d.ServiceType == typeof(IApiKeyStore) && !d.IsKeyedService).ImplementationInstance!;
            _users = (IUserStore)services.Last(d => d.ServiceType == typeof(IUserStore) && !d.IsKeyedService).ImplementationInstance!;
            AddKey(PartnerKey, _partnerTenantId, "partner-admin", ApiKeyType.Standard);
            AddKey(ManagementKey, HostTenantId, null, ApiKeyType.Management);
            apiKeys.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(_keys.GetValueOrDefault(ci.ArgAt<string>(0))));
        });

        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = HostTenantId, Name = "Test Platform", Status = TenantStatus.Active, Type = TenantType.Platform,
            ParentTenantId = null, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }).AsTask().GetAwaiter().GetResult();
        return host;
    }

    private void AddKey(string raw, string tenantId, string? userId, ApiKeyType type) =>
        _keys[Hash(raw)] = new ApiKey
        {
            KeyId = EntityId.From("key-" + raw), TenantId = new TenantId(tenantId), Name = raw, HashedKey = Hash(raw),
            Scopes = type == ApiKeyType.Management ? ["platform:*"] : ["*"], KeyType = type,
            UserId = userId is null ? null : EntityId.From(userId), CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    public static Queue Queue(string tenant, string name, string? id = null) => new()
    {
        QueueId = id is null ? EntityId.New() : EntityId.From(id), TenantId = new TenantId(tenant), Name = name,
        IsActive = true, WrapUp = new WrapUpConfig(), CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Stores a tenant as a pre-upgrade deployment holds it: straight into the tenant store, no id rule.</summary>
    public async Task SeedTenantAsync(string id, TenantType type, string parent, TenantStatus status = TenantStatus.Active)
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        await store.UpsertAsync(new Tenant
        {
            TenantId = id, Name = id, Status = TenantStatus.Active, Type = type, ParentTenantId = parent,
            Metadata = new Dictionary<string, string> { ["Plan"] = TenantPlan.Enterprise.ToString() },
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (status != TenantStatus.Active)
            await store.UpdateStatusAsync(id, status);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(Services, id);
    }

    /// <summary>
    /// Seeds <paramref name="tenant"/>'s agents, queues, memberships, listen slots and trunks in Platform's stores and,
    /// through Pro's engine, in the realtime tables; returns the realtime rows written, after checking they exist.
    /// </summary>
    public async Task<SeededRealtimeRows> SeedRealtimeAsync(string tenant, string[]? agents = null, string[]? queues = null,
        (string Queue, string Agent)[]? members = null, string[]? listenUsers = null, int trunks = 0, string? trunkMatchHost = null)
    {
        var before = await RowsAsync();
        var trunkIds = new List<long>();
        using (var scope = Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var sync = sp.GetRequiredService<IRealtimeSyncService>();
            foreach (var a in agents ?? [])
            {
                await InsertAgentAsync(tenant, a, "1" + a[..4], "pw-" + a[..8]);
                await sync.SyncAgentAsync(tenant, a, a, "1" + a[..4], "pw-" + a[..8]);
            }
            foreach (var q in queues ?? [])
            {
                await sp.GetRequiredService<IQueueStore>().SaveAsync(Queue(tenant, q), default);
                await sync.SyncQueueAsync(tenant, q, new RealtimeQueueOptions());
            }
            foreach (var (q, a) in members ?? [])
                await sync.AddQueueMemberAsync(tenant, q, a, a);
            foreach (var u in listenUsers ?? [])
            {
                var saved = await sp.GetRequiredService<ISupervisorListenProfileStore>().SaveAsync(new SupervisorListenProfile
                {
                    TenantId = new TenantId(tenant), UserId = EntityId.From(u), SipExtension = "8" + u[..4], SipPassword = "pw-" + u[..8],
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                }, default);
                saved.Should().BeTrue();
                await sync.SyncAgentAsync(tenant, "sup-" + u, "listen " + u, "8" + u[..4], "pw-" + u[..8]);
            }
            for (var i = 0; i < trunks; i++)
            {
                trunkIds.Add(await sp.GetRequiredService<TrunkStoreBase>().CreateAsync(new Trunk
                {
                    Name = $"{tenant}-trunk{i}", AuthUsername = "user" + i, AuthPassword = "secret" + i,
                    RegistrationUri = "sip:registrar.example.test", ClientUri = $"sip:user{i}@registrar.example.test",
                    MatchHost = trunkMatchHost,
                }, tenant));
            }
        }
        var after = await RowsAsync();
        var trunkIdsText = trunkIds.Select(id => $"t-{id}").ToHashSet(StringComparer.Ordinal);
        var trunkRows = after.Except(before).Where(r => trunkIdsText.Any(t =>
            r.EndsWith(":" + t, StringComparison.Ordinal) || r.EndsWith(":auth-" + t, StringComparison.Ordinal)
            || r.EndsWith(":reg-" + t, StringComparison.Ordinal) || r.EndsWith(":ipauth-" + t, StringComparison.Ordinal))).ToList();
        foreach (var t in trunkIdsText)
            trunkRows.Should().Contain([$"ps_endpoints:{t}", $"ps_auths:auth-{t}", $"ps_registrations:reg-{t}"], "the trunk is seeded");
        var expected = new List<string>();
        var seeded = new SeededRealtimeRows(tenant, expected, trunkRows);
        foreach (var a in agents ?? [])
            expected.AddRange(seeded.AgentRows(a));
        foreach (var u in listenUsers ?? [])
            expected.AddRange([$"ps_endpoints:{tenant}-agent-sup-{u}", $"ps_auths:auth-{tenant}-agent-sup-{u}"]);
        expected.AddRange((queues ?? []).Select(q => $"queues:{tenant}-{q}"));
        expected.AddRange((members ?? []).Select(m => $"queue_members:{tenant}-{m.Queue}|PJSIP/{tenant}-agent-{m.Agent}"));
        expected.AddRange(trunkRows);
        after.Should().Contain(expected, "every seeded row is in the realtime tables");
        return seeded;
    }

    /// <summary>
    /// Stores an agent row as the licensed-agent writer would (<c>IAgentStore.SaveAsync</c> only updates an existing
    /// agent); every nullable parameter carries its type.
    /// </summary>
    public async Task InsertAgentAsync(string tenant, string agentId, string? extension, string? sipPassword)
    {
        await using var db = NpgsqlDataSource.Create(_connectionString);
        await using var cmd = db.CreateCommand(
            "INSERT INTO agents (agent_id, tenant_id, user_id, display_name, extension, sip_password, created_at) " +
            "VALUES (@AgentId, @TenantId, @UserId, @DisplayName, @Extension, @SipPassword, now())");
        cmd.Parameters.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agentId });
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenant });
        cmd.Parameters.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = agentId });
        cmd.Parameters.Add(new NpgsqlParameter("DisplayName", NpgsqlDbType.Text) { Value = agentId });
        cmd.Parameters.Add(new NpgsqlParameter("Extension", NpgsqlDbType.Varchar) { Value = (object?)extension ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("SipPassword", NpgsqlDbType.Varchar) { Value = (object?)sipPassword ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Every row of the realtime tables, as <c>table:id</c>.</summary>
    public async Task<HashSet<string>> RowsAsync()
    {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        await using var db = NpgsqlDataSource.Create(_connectionString);
        await using var cmd = db.CreateCommand(RowsSql);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    public HttpClient ManagementClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ManagementKey}");
        return client;
    }

    public async Task PartnerSuspendAsync(string customerId)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {PartnerKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", _partnerTenantId);
        using var response = await client.PostAsJsonAsync($"/api/v1/partner/customers/{customerId}/suspend", new { reason = "test" });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Creates <paramref name="name"/> and deletes it through the admin queue routes, as the tenant's admin.</summary>
    public async Task CreateAndDeleteQueueAsync(string tenantId, string name)
    {
        var raw = "rt-live-admin-" + tenantId;
        var userId = "admin-" + tenantId;
        await _users!.CreateAsync(new User
        {
            UserId = EntityId.From(userId), TenantId = new TenantId(tenantId), Email = $"{userId}@rt-live.test",
            DisplayName = userId, Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
        }, default);
        AddKey(raw, tenantId, userId, ApiKeyType.Standard);
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {raw}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        using var created = await client.PostAsJsonAsync("/api/v1/admin/queues", new { name });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        (await RowsAsync()).Should().Contain($"queues:{tenantId}-{name}", "the queue route syncs the new queue");
        var id = JsonNode.Parse(body)!["id"]!.GetValue<string>();
        using var deleted = await client.DeleteAsync(new Uri($"/api/v1/admin/queues/{id}", UriKind.Relative));
        deleted.IsSuccessStatusCode.Should().BeTrue(await deleted.Content.ReadAsStringAsync());
    }

    /// <summary>Starts Pro's real reconciler, waits for the end of its first cycle (its own log line), and stops it.</summary>
    public async Task RunOneReconcileCycleAsync()
    {
        var reconciler = (IHostedService)Services.GetRequiredService(_reconcilerType!);
        await reconciler.StartAsync(default);
        var line = await _cycle.Ended.WaitAsync(TimeSpan.FromSeconds(60));
        await reconciler.StopAsync(default);
        line.Should().Contain("cycle complete", "the reconcile cycle ran to its end");
    }

    /// <summary>The Warning records whose message names <paramref name="value"/>.</summary>
    public IReadOnlyList<CapturedLogRecord> WarningsNaming(string value) =>
        [.. Logs.RecordsContaining(value).Where(r => r.Level == LogLevel.Warning)];

    /// <summary>Completes on the reconciler's end-of-cycle line ("complete" or "failed").</summary>
    private sealed class ReconcileCycleSignal : ILoggerProvider
    {
        private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Ended => _ended.Task;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _ended);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, TaskCompletionSource<string> ended) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category == ReconcilerTypeName;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (category == ReconcilerTypeName && message.Contains("Reconciliation cycle", StringComparison.Ordinal))
                    ended.TrySetResult(message);
            }
        }
    }
}

/// <summary>Pro's real <see cref="IRealtimeSyncService"/>, with each completed write recorded as <c>Operation:tenant[:target]</c>.</summary>
internal sealed class RecordingRealtimeSync(IRealtimeSyncService inner) : IRealtimeSyncService
{
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentQueue<string> _attempts = new();
    private readonly List<(Func<string, bool> Match, TaskCompletionSource Done)> _waiters = [];

    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>Every call made, recorded before the write runs: a write that throws is still an attempt.</summary>
    public IReadOnlyList<string> Attempts => [.. _attempts];

    public IObservable<RealtimeSyncEvent> Events => inner.Events;

    /// <summary>Completes once a recorded call matches <paramref name="match"/> (a call already recorded counts).</summary>
    public Task WaitFor(Func<string, bool> match)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters)
        {
            if (_calls.Any(match))
                done.TrySetResult();
            else
                _waiters.Add((match, done));
        }
        return done.Task;
    }

    private async ValueTask Run(string call, Func<ValueTask> write)
    {
        _attempts.Enqueue(call);
        await write();
        lock (_waiters)
        {
            _calls.Enqueue(call);
            foreach (var (match, done) in _waiters.Where(w => w.Match(call)).ToList())
                done.TrySetResult();
        }
    }

    public ValueTask SyncAgentAsync(string tenantId, string agentId, string displayName, string extension, string sipPassword,
        long? profileId = null, CancellationToken ct = default) =>
        Run($"SyncAgent:{tenantId}:{agentId}", () => inner.SyncAgentAsync(tenantId, agentId, displayName, extension, sipPassword, profileId, ct));

    public ValueTask RemoveAgentAsync(string tenantId, string agentId, CancellationToken ct = default) =>
        Run($"RemoveAgent:{tenantId}:{agentId}", () => inner.RemoveAgentAsync(tenantId, agentId, ct));

    public ValueTask SyncQueueAsync(string tenantId, string queueName, RealtimeQueueOptions options, CancellationToken ct = default) =>
        Run($"SyncQueue:{tenantId}:{queueName}", () => inner.SyncQueueAsync(tenantId, queueName, options, ct));

    public ValueTask RemoveQueueAsync(string tenantId, string queueName, CancellationToken ct = default) =>
        Run($"RemoveQueue:{tenantId}:{queueName}", () => inner.RemoveQueueAsync(tenantId, queueName, ct));

    public ValueTask AddQueueMemberAsync(string tenantId, string queueName, string agentId, string displayName, int penalty = 0,
        IReadOnlyList<string>? allowedChannels = null, CancellationToken ct = default) =>
        Run($"AddQueueMember:{tenantId}:{queueName}/{agentId}",
            () => inner.AddQueueMemberAsync(tenantId, queueName, agentId, displayName, penalty, allowedChannels, ct));

    public ValueTask RemoveQueueMemberAsync(string tenantId, string queueName, string agentId, CancellationToken ct = default) =>
        Run($"RemoveQueueMember:{tenantId}:{queueName}/{agentId}", () => inner.RemoveQueueMemberAsync(tenantId, queueName, agentId, ct));

    public ValueTask<bool> QueueMemberExistsAsync(string tenantId, string queueName, string agentId, CancellationToken ct = default) =>
        inner.QueueMemberExistsAsync(tenantId, queueName, agentId, ct);

    public ValueTask SyncAgentPausedAsync(string tenantId, string agentId, bool paused, CancellationToken ct = default) =>
        Run($"SyncAgentPaused:{tenantId}:{agentId}", () => inner.SyncAgentPausedAsync(tenantId, agentId, paused, ct));

    public ValueTask SyncTrunkAsync(string tenantId, Trunk trunk, CancellationToken ct = default) =>
        Run($"SyncTrunk:{tenantId}:{trunk.Id}", () => inner.SyncTrunkAsync(tenantId, trunk, ct));

    public ValueTask RemoveTrunkAsync(string tenantId, long trunkId, CancellationToken ct = default) =>
        Run($"RemoveTrunk:{tenantId}:{trunkId}", () => inner.RemoveTrunkAsync(tenantId, trunkId, ct));

    public ValueTask SyncAgentBatchAsync(string tenantId, IReadOnlyList<AgentSyncRequest> agents, CancellationToken ct = default) =>
        Run($"SyncAgentBatch:{tenantId}", () => inner.SyncAgentBatchAsync(tenantId, agents, ct));

    public ValueTask SyncQueueBatchAsync(string tenantId, IReadOnlyList<QueueSyncRequest> queues, CancellationToken ct = default) =>
        Run($"SyncQueueBatch:{tenantId}", () => inner.SyncQueueBatchAsync(tenantId, queues, ct));

    public ValueTask ProvisionTenantAsync(string tenantId, CancellationToken ct = default) =>
        Run($"ProvisionTenant:{tenantId}", () => inner.ProvisionTenantAsync(tenantId, ct));

    public ValueTask CleanupTenantAsync(string tenantId, CancellationToken ct = default) =>
        Run($"CleanupTenant:{tenantId}", () => inner.CleanupTenantAsync(tenantId, ct));
}

/// <summary>Platform's real <see cref="ITenantLifecycleDispatcher"/>; completes a waiter once a suspension has fully returned.</summary>
internal sealed class SignallingLifecycleDispatcher(ITenantLifecycleDispatcher inner) : ITenantLifecycleDispatcher
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _suspended = new(StringComparer.Ordinal);

    /// <summary>Completes when <see cref="SuspendedAsync"/> for <paramref name="tenantId"/> has returned (or already had).</summary>
    public Task WaitForSuspended(string tenantId) => Suspension(tenantId).Task;

    public ValueTask CreatedAsync(Tenant tenant, CancellationToken ct) => inner.CreatedAsync(tenant, ct);

    public async ValueTask SuspendedAsync(string tenantId, CancellationToken ct)
    {
        await inner.SuspendedAsync(tenantId, ct);
        Suspension(tenantId).TrySetResult();
    }

    public ValueTask DeletedAsync(string tenantId, CancellationToken ct) => inner.DeletedAsync(tenantId, ct);

    private TaskCompletionSource Suspension(string tenantId) =>
        _suspended.GetOrAdd(tenantId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
