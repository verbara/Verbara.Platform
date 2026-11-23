using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Engine;

namespace Verbara.Platform.Api.Tests.Realtime;

/// <summary>A registry whose stored ids are fixed by the test.</summary>
internal sealed class FixedTenantIdRegistry(params StoredTenantId[] ids) : ITenantIdRegistry
{
    public ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct) => new(ids);
}

/// <summary>A registry that reads the test's list on each call.</summary>
internal sealed class ListTenantIdRegistry(List<StoredTenantId> ids) : ITenantIdRegistry
{
    public ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct) => new(ids.ToList());
}

/// <summary>A registry that cannot be read.</summary>
internal sealed class UnreadableTenantIdRegistry : ITenantIdRegistry
{
    public ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct) =>
        throw new InvalidOperationException("tenant registry unavailable");

    public ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct) =>
        throw new InvalidOperationException("tenant registry unavailable");
}

/// <summary>A lifecycle handler outside Platform that records what reached it.</summary>
internal sealed class RecordingExternalHandler : ITenantLifecycleHandler
{
    public List<string> Calls { get; } = [];

    public ValueTask OnTenantCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default) => Record($"created:{tenant.TenantId}");

    public ValueTask OnTenantSuspendedAsync(string tenantId, CancellationToken cancellationToken = default) => Record($"suspended:{tenantId}");

    public ValueTask OnTenantDeletedAsync(string tenantId, CancellationToken cancellationToken = default) => Record($"deleted:{tenantId}");

    private ValueTask Record(string call)
    {
        lock (Calls) Calls.Add(call);
        return ValueTask.CompletedTask;
    }
}

internal static class IsolationKit
{
    public static StoredTenantId Id(string id, TenantStatus status = TenantStatus.Active) => new(id, status);

    public static async Task SeedTenantAsync(IServiceProvider services, string id, TenantStatus status = TenantStatus.Active,
        TenantType type = TenantType.Customer, string? parent = "platform")
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        await store.UpsertAsync(new Tenant
        {
            TenantId = id, Name = id, Status = TenantStatus.Active, Type = type, ParentTenantId = parent,
            Metadata = new Dictionary<string, string> { ["Plan"] = TenantPlan.Enterprise.ToString() },
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (status != TenantStatus.Active)
            await store.UpdateStatusAsync(id, status);
    }

    public static Agent Agent(string tenant, string id, string? ext = null, string? pw = null) => new()
    {
        AgentId = EntityId.From(id), TenantId = new TenantId(tenant), UserId = EntityId.From("u-" + id),
        DisplayName = id, Extension = ext, SipPassword = pw, State = AgentState.Offline, CreatedAt = DateTimeOffset.UtcNow,
    };

    public static Queue Queue(string tenant, string name, bool active = true, string? id = null) => new()
    {
        QueueId = id is null ? EntityId.New() : EntityId.From(id), TenantId = new TenantId(tenant), Name = name,
        IsActive = active, WrapUp = new WrapUpConfig(), CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Seeds agents, queues, listen slots and trunks of <paramref name="tenant"/> through the host's stores.</summary>
    public static async Task<long[]> SeedRealtimeEntitiesAsync(IServiceProvider services, string tenant,
        string[] agents, string[] queues, string[] listenUsers, int trunks)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        foreach (var a in agents)
            await sp.GetRequiredService<IAgentStore>().SaveAsync(Agent(tenant, a, "10" + a.Length, "pw"), default);
        foreach (var q in queues)
            await sp.GetRequiredService<IQueueStore>().SaveAsync(Queue(tenant, q), default);
        foreach (var u in listenUsers)
        {
            await sp.GetRequiredService<ISupervisorListenProfileStore>().SaveAsync(new SupervisorListenProfile
            {
                TenantId = new TenantId(tenant), UserId = EntityId.From(u), SipExtension = "sl" + u, SipPassword = "pw",
                CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
            }, default);
        }
        var ids = new List<long>();
        for (var i = 0; i < trunks; i++)
            ids.Add(await sp.GetRequiredService<TrunkStoreBase>().CreateAsync(new Trunk { Name = $"{tenant}-trunk{i}" }, tenant));
        return [.. ids];
    }
}

// ─── 1.2: the audit, the health check and the reconciler's tenant list ──────────────────────────────────────────

public sealed class TenantIdAuditTests
{
    private static Task<TenantIdAuditReport> Run(params StoredTenantId[] ids) =>
        new TenantIdAudit(new FixedTenantIdRegistry(ids)).RunAsync(default).AsTask();

    [Fact]
    public async Task RunAsync_ShouldFlagEachReason_WhenStoredIdsBreakTheRule()
    {
        var report = await Run(IsolationKit.Id("platform"), IsolationKit.Id("Bad_Id"), IsolationKit.Id("t"),
            IsolationKit.Id("globex"), IsolationKit.Id("GLOBEX"), IsolationKit.Id("acme"), IsolationKit.Id("acme-corp"),
            IsolationKit.Id("initech"));

        report.Flags.Select(f => (f.TenantId, f.Reason)).Should().BeEquivalentTo(new[]
        {
            ("Bad_Id", TenantIdFlagReason.NotASlug),
            ("t", TenantIdFlagReason.Reserved),
            ("GLOBEX", TenantIdFlagReason.NotASlug),
            ("globex", TenantIdFlagReason.CaseVariant),
            ("GLOBEX", TenantIdFlagReason.CaseVariant),
            ("acme", TenantIdFlagReason.PrefixPair),
            ("acme-corp", TenantIdFlagReason.PrefixPair),
        });
        report.IsFlagged("platform").Should().BeFalse("the host tenant itself is not reserved against itself");
        report.IsFlagged("initech").Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ShouldNameEachPairOnceAsShorterThenLonger_WhenTwoIdsOverlap()
    {
        var report = await Run(IsolationKit.Id("acme-corp"), IsolationKit.Id("acme"));

        var pair = report.Pairs.Should().ContainSingle().Subject;
        pair.Shorter.TenantId.Should().Be("acme");
        pair.Longer.TenantId.Should().Be("acme-corp");
        pair.Reason.Should().Be(TenantIdFlagReason.PrefixPair);
    }

    [Fact]
    public async Task RunAsync_ShouldStillFlagBothMembers_WhenOneMemberIsDeleted()
    {
        var report = await Run(IsolationKit.Id("acme", TenantStatus.Deleted), IsolationKit.Id("acme-corp"));

        report.IsFlagged("acme").Should().BeTrue();
        report.IsFlagged("acme-corp").Should().BeTrue("deleting one member of a pair unflags nothing");
    }

    /// <summary>The quadratic definition: every unordered pair for which <see cref="TenantIdRule.Overlaps"/> holds.</summary>
    private static HashSet<(string, string)> OraclePairs(StoredTenantId[] ids)
    {
        var pairs = new HashSet<(string, string)>();
        for (var i = 0; i < ids.Length; i++)
            for (var j = i + 1; j < ids.Length; j++)
                if (TenantIdRule.Overlaps(ids[i].TenantId, ids[j].TenantId))
                    pairs.Add(Ordered(ids[i].TenantId, ids[j].TenantId));
        return pairs;
    }

    private static (string, string) Ordered(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    [Fact]
    public void OverlappingPairs_ShouldFindTheNonAdjacentPair_WhenAnUnrelatedExtensionSortsBetween()
    {
        // Sorted: x, x-a, x-a1, x-b, x0, x0-x: neighbour comparison alone misses x / x-b, x / x-a1 and x-a / x-a1 is no pair.
        StoredTenantId[] ids = [IsolationKit.Id("x-b"), IsolationKit.Id("x0"), IsolationKit.Id("x"), IsolationKit.Id("x-a1"),
            IsolationKit.Id("x-a"), IsolationKit.Id("x0-x")];

        TenantIdAudit.OverlappingPairs(ids).Select(p => Ordered(p.A.TenantId, p.B.TenantId)).Should().BeEquivalentTo(new[]
        {
            ("x", "x-a"), ("x", "x-a1"), ("x", "x-b"), ("x0", "x0-x"),
        });
    }

    [Fact]
    public async Task RunAsync_ShouldMatchTheQuadraticDefinition_WhenStoredIdsAreRandom()
    {
        // Property test: a small alphabet makes prefixes, case variants and hyphen boundaries frequent; the non-ASCII
        // letters check that the sort key folds case as OrdinalIgnoreCase does.
        const string alphabet = "ab-A_.\u0131\u0130k\u212A";
        var random = new Random(20261123);
        for (var round = 0; round < 1500; round++)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var size = random.Next(0, 40);
            while (set.Count < size)
                set.Add(new string([.. Enumerable.Range(0, random.Next(1, 6)).Select(_ => alphabet[random.Next(alphabet.Length)])]));
            var ids = set.Select(i => IsolationKit.Id(i)).ToArray();

            var expected = OraclePairs(ids);
            var found = TenantIdAudit.OverlappingPairs(ids).Select(p => Ordered(p.A.TenantId, p.B.TenantId)).ToList();
            found.Should().OnlyHaveUniqueItems($"round {round}: [{string.Join(' ', set)}]");
            found.Should().BeEquivalentTo(expected, $"round {round}: [{string.Join(' ', set)}]");

            var report = await Run(ids);
            report.Pairs.Should().HaveCount(expected.Count);
            var paired = expected.SelectMany(p => new[] { p.Item1, p.Item2 }).ToHashSet(StringComparer.Ordinal);
            report.Flags.Where(f => f.Reason is TenantIdFlagReason.PrefixPair or TenantIdFlagReason.CaseVariant)
                .Select(f => f.TenantId).ToHashSet(StringComparer.Ordinal).Should().BeEquivalentTo(paired, $"round {round}");
        }
    }

    [Fact]
    public async Task RunAsync_ShouldFlagTheHostTenant_WhenAStoredIdExtendsIt()
    {
        var report = await Run(IsolationKit.Id("platform"), IsolationKit.Id("platform-x"));

        report.IsFlagged("platform").Should().BeTrue();
        report.IsFlagged("platform-x").Should().BeTrue();
    }
}

/// <summary>A logger that records the Warning messages it receives.</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            lock (Warnings) Warnings.Add(formatter(state, exception));
    }
}

public sealed class TenantIdHealthCheckTests
{
    private static IConfiguration Config(params string[] realtimeTenants) => new ConfigurationBuilder()
        .AddInMemoryCollection(realtimeTenants.Select((t, i) => new KeyValuePair<string, string?>($"Realtime:TenantIds:{i}", t)))
        .Build();

    private static TenantIdHealthCheck NewCheck(IConfiguration config, RecordingLogger<TenantIdHealthCheck>? logger,
        params StoredTenantId[] ids) =>
        new(new TenantIdAudit(new FixedTenantIdRegistry(ids)), config,
            (Microsoft.Extensions.Logging.ILogger<TenantIdHealthCheck>?)logger ?? NullLogger<TenantIdHealthCheck>.Instance);

    private static Task<HealthCheckResult> Check(IConfiguration config, params StoredTenantId[] ids) =>
        NewCheck(config, null, ids).CheckHealthAsync(new HealthCheckContext());

    private static IEnumerable<string> Text(HealthCheckResult result) =>
        result.Data.Keys.Concat(result.Data.Values.Select(v => v.ToString() ?? "")).Append(result.Description ?? "");

    [Fact]
    public async Task CheckHealthAsync_ShouldBeDegradedWithCountsAndNoTenantId_WhenAPairIsStored()
    {
        var result = await Check(Config("acme-corp"), IsolationKit.Id("platform"), IsolationKit.Id("acme"),
            IsolationKit.Id("acme-corp", TenantStatus.Suspended));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("2 stored tenant ids break the tenant-id rule; see the Api log.");
        result.Data["flaggedTenantIds"].Should().Be(2);
        result.Data["pairs"].Should().Be(1);
        result.Data["unstoredRealtimeTenantIds"].Should().Be(0);
        Text(result).Should().NotContain(t => t.Contains("acme", StringComparison.OrdinalIgnoreCase)
            || t.Contains(nameof(TenantStatus.Suspended), StringComparison.Ordinal)
            || t.Contains(TenantIdFlagReason.PrefixPair, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldBeDegradedWithACountAndNoTenantId_WhenTheDefaultRealtimeTenantIsNotStored()
    {
        var result = await Check(new ConfigurationBuilder().Build(), IsolationKit.Id("platform"), IsolationKit.Id("demo-corp"));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("1 configured realtime tenant id is not stored; see the Api log.");
        result.Data["unstoredRealtimeTenantIds"].Should().Be(1);
        Text(result).Should().NotContain(t => t.Contains("demo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldLogTheIdsOncePerChangeOfTheFlaggedSet_WhenProbedRepeatedly()
    {
        var logger = new RecordingLogger<TenantIdHealthCheck>();
        var ids = new List<StoredTenantId> { IsolationKit.Id("platform"), IsolationKit.Id("acme"), IsolationKit.Id("acme-corp") };
        var check = new TenantIdHealthCheck(new TenantIdAudit(new ListTenantIdRegistry(ids)), Config("acme"), logger);

        await check.CheckHealthAsync(new HealthCheckContext());
        await check.CheckHealthAsync(new HealthCheckContext());
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("acme / acme-corp").And.Contain(TenantIdFlagReason.PrefixPair);

        ids.Add(IsolationKit.Id("t"));
        await check.CheckHealthAsync(new HealthCheckContext());
        await check.CheckHealthAsync(new HealthCheckContext());
        logger.Warnings.Should().HaveCount(2);
        logger.Warnings[1].Should().Contain("t: " + TenantIdFlagReason.Reserved);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldBeHealthy_WhenEveryIdIsCleanAndEveryConfiguredTenantIsStored()
    {
        var result = await Check(Config("acme"), IsolationKit.Id("platform"), IsolationKit.Id("acme"), IsolationKit.Id("globex"));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Readiness_ShouldAnswer200AndReportTheTenantIdCheckDegraded_WhenAPairIsStored()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        _ = factory.Server;
        await IsolationKit.SeedTenantAsync(factory.Services, "acme");
        await IsolationKit.SeedTenantAsync(factory.Services, "acme-corp");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["checks"]!["tenant-ids"]?["status"]?.GetValue<string>().Should().Be("Degraded", body);
        JsonNode.Parse(body)!["checks"]!["tenant-ids"]!["data"]!["pairs"]!.GetValue<int>().Should().Be(1, body);
    }

    [Fact]
    public async Task Readiness_ShouldCarryNoTenantId_WhenTheTenantIdCheckIsDegraded()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        _ = factory.Server;
        await IsolationKit.SeedTenantAsync(factory.Services, "acme");
        await IsolationKit.SeedTenantAsync(factory.Services, "acme-corp", TenantStatus.Suspended);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("acme", "readiness is anonymous and served on the public gateway");
        body.Should().NotContain(TenantIdFlagReason.PrefixPair);
    }
}

public sealed class ReconciledTenantListTests
{
    private static readonly string[] PairAndControl = ["acme", "acme-corp", "globex"];

    private static async Task<IReadOnlyList<string>> ActiveTenantsAsync(string[]? configured, Func<IServiceProvider, Task> seed,
        bool unreadableAudit = false)
    {
        using var factory = new AuthenticatedPlatformApiFactory().WithWebHostBuilder(b =>
        {
            if (configured is not null)
                for (var i = 0; i < configured.Length; i++)
                    b.UseSetting($"Realtime:TenantIds:{i}", configured[i]);
            if (unreadableAudit)
                b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ITenantIdRegistry, UnreadableTenantIdRegistry>()));
        });
        _ = factory.Server;
        await seed(factory.Services);
        return await factory.Services.GetRequiredService<IDesiredStateProvider>().GetActiveTenantIdsAsync();
    }

    [Fact]
    public async Task GetActiveTenantIdsAsync_ShouldLeaveOutBothMembers_WhenAPairIsListed()
    {
        var result = await ActiveTenantsAsync(["acme", "acme-corp", "globex"], async sp =>
        {
            foreach (var t in PairAndControl)
                await IsolationKit.SeedTenantAsync(sp, t);
        });

        result.Should().BeEquivalentTo(["globex"]);
    }

    [Fact]
    public async Task GetActiveTenantIdsAsync_ShouldReturnNoTenant_WhenOnlyOneMemberOfAPairIsListed()
    {
        var result = await ActiveTenantsAsync(["acme"], async sp =>
        {
            await IsolationKit.SeedTenantAsync(sp, "acme");
            await IsolationKit.SeedTenantAsync(sp, "acme-corp");
        });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveTenantIdsAsync_ShouldReturnNoTenant_WhenTheDefaultTenantIsNotStored()
    {
        var result = await ActiveTenantsAsync(null, async sp => await IsolationKit.SeedTenantAsync(sp, "demo-corp"));

        result.Should().BeEmpty("the default 'demo' is configured but not stored, so it is never reconciled");
    }

    [Fact]
    public async Task GetActiveTenantIdsAsync_ShouldLeaveOutADeletedTenant_WhenItIsListed()
    {
        var result = await ActiveTenantsAsync(["globex", "initech"], async sp =>
        {
            await IsolationKit.SeedTenantAsync(sp, "globex");
            await IsolationKit.SeedTenantAsync(sp, "initech", TenantStatus.Deleted);
        });

        result.Should().BeEquivalentTo(["globex"]);
    }

    [Fact]
    public async Task GetActiveTenantIdsAsync_ShouldReturnNoTenant_WhenTheListedTenantsPartnerIsDeleted()
    {
        var result = await ActiveTenantsAsync(["acme-corp"], async sp =>
        {
            await IsolationKit.SeedTenantAsync(sp, "acme", TenantStatus.Deleted);
            await IsolationKit.SeedTenantAsync(sp, "acme-corp");
        });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveTenantIdsAsync_ShouldReturnNoTenant_WhenTheAuditCannotBeRead()
    {
        var result = await ActiveTenantsAsync(["globex"], async sp => await IsolationKit.SeedTenantAsync(sp, "globex"),
            unreadableAudit: true);

        result.Should().BeEmpty("an unreadable audit is fail-closed");
    }
}

// ─── 1.3: the dispatcher, the exact removal and the queue-delete fence ───────────────────────────────────────────

public sealed class FlaggedTenantLifecycleTests
{
    private const string Acme = "acme";
    private const string AcmeCorp = "acme-corp";

    private sealed class Rig : IDisposable
    {
        public Rig(bool externalRecorder)
        {
            Recorder = new RecordingExternalHandler();
            var recorder = Recorder;
            Factory = externalRecorder
                ? new PlatformAdminApiFactory().WithWebHostBuilder(b => b.ConfigureTestServices(
                    s => s.AddSingleton<ITenantLifecycleHandler>(recorder)))
                : new PlatformAdminApiFactory();
            Client = PlatformAdminApiFactory.CreatePlatformAdminClient(Factory);
            Sync = Factory.Services.GetRequiredService<IRealtimeSyncService>();
        }

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public IRealtimeSyncService Sync { get; }
        public RecordingExternalHandler Recorder { get; }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    [Fact]
    public async Task SuspendTenant_ShouldRemoveAFlaggedTenantsRowsByExactIdAndNeverItsQueues_WhenItIsSuspended()
    {
        using var rig = new Rig(externalRecorder: true);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, Acme);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, AcmeCorp);
        var trunks = await IsolationKit.SeedRealtimeEntitiesAsync(rig.Factory.Services, Acme, ["a1"], ["corp-x"], ["u1"], 1);
        await IsolationKit.SeedRealtimeEntitiesAsync(rig.Factory.Services, AcmeCorp, ["b1"], ["x"], [], 0);
        rig.Sync.ClearReceivedCalls();

        using var response = await rig.Client.PostAsync(new Uri($"/api/management/tenants/{Acme}/suspend", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await rig.Sync.Received(1).RemoveQueueMemberAsync(Acme, "corp-x", "a1", Arg.Any<CancellationToken>());
        await rig.Sync.Received(1).RemoveAgentAsync(Acme, "a1", Arg.Any<CancellationToken>());
        await rig.Sync.Received(1).RemoveAgentAsync(Acme, "sup-u1", Arg.Any<CancellationToken>());
        await rig.Sync.Received(1).RemoveTrunkAsync(Acme, trunks[0], Arg.Any<CancellationToken>());
        await rig.Sync.DidNotReceive().RemoveQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await rig.Sync.DidNotReceive().CleanupTenantAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        rig.Recorder.Calls.Should().BeEmpty("a flagged tenant reaches only Platform's own handlers");
    }

    [Fact]
    public async Task DeleteTenant_ShouldRemoveAFlaggedTenantsSlotsAndTrunksAndNoQueue_WhenItIsDeleted()
    {
        using var rig = new Rig(externalRecorder: true);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, Acme);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, AcmeCorp);
        var trunks = await IsolationKit.SeedRealtimeEntitiesAsync(rig.Factory.Services, Acme, [], ["support", "corp-x"], ["u1"], 1);
        await IsolationKit.SeedRealtimeEntitiesAsync(rig.Factory.Services, AcmeCorp, ["b1"], ["x"], [], 0);
        rig.Sync.ClearReceivedCalls();

        using var response = await rig.Client.DeleteAsync(new Uri($"/api/management/tenants/{Acme}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        await rig.Sync.Received(1).RemoveAgentAsync(Acme, "sup-u1", Arg.Any<CancellationToken>());
        await rig.Sync.Received(1).RemoveTrunkAsync(Acme, trunks[0], Arg.Any<CancellationToken>());
        await rig.Sync.DidNotReceive().RemoveQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await rig.Sync.DidNotReceive().CleanupTenantAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        rig.Recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SuspendTenant_ShouldReachEveryHandler_WhenTheTenantIsNotFlagged()
    {
        using var rig = new Rig(externalRecorder: true);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, "globex");
        rig.Sync.ClearReceivedCalls();

        using var response = await rig.Client.PostAsync(new Uri("/api/management/tenants/globex/suspend", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await rig.Sync.Received(1).CleanupTenantAsync("globex", Arg.Any<CancellationToken>());
        rig.Recorder.Calls.Should().Equal("suspended:globex");
    }

    [Fact]
    public async Task DunningEscalation_ShouldRemoveAFlaggedTenantsRowsByExactId_WhenItSuspendsTheTenant()
    {
        using var rig = new Rig(externalRecorder: false);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, Acme, status: TenantStatus.Warning);
        await IsolationKit.SeedTenantAsync(rig.Factory.Services, AcmeCorp);
        await IsolationKit.SeedRealtimeEntitiesAsync(rig.Factory.Services, Acme, ["a1"], [], [], 0);
        using (var scope = rig.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Verbara.Platform.Billing.IDunningStore>().UpsertAsync(
                new Verbara.Platform.Billing.DunningRecord
                {
                    DunningId = EntityId.New().Value, TenantId = Acme, InvoiceId = EntityId.New().Value,
                    CurrentStage = TenantStatus.Warning, StartedAt = DateTimeOffset.UtcNow.AddDays(-20),
                }, default);
        }
        rig.Sync.ClearReceivedCalls();
        var dunning = new Verbara.Platform.Billing.DunningService(
            rig.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<Verbara.Platform.Billing.DunningService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Verbara.Platform.Billing.DunningConfig()));

        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2012 // NSubstitute setup chains .Returns() on the ValueTask
        rig.Sync.RemoveAgentAsync(Acme, "a1", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            dispatched.TrySetResult();
            return ValueTask.CompletedTask;
        });
#pragma warning restore CA2012

        await dunning.StartAsync(default);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await dunning.StopAsync(default);
        var tenants = rig.Factory.Services.GetRequiredService<ITenantStore>();

        (await tenants.GetAsync(Acme))!.Status.Should().Be(TenantStatus.Suspended);
        await rig.Sync.Received(1).RemoveAgentAsync(Acme, "a1", Arg.Any<CancellationToken>());
        await rig.Sync.DidNotReceive().CleanupTenantAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LifecycleHandlers_ShouldHaveExactlyOneHandlerOutsidePlatform_WhenTheApiIsComposed()
    {
        using var factory = new PlatformAdminApiFactory();

        var handlers = factory.Services.GetServices<ITenantLifecycleHandler>().ToList();

        handlers.Count(h => h is not IPlatformTenantLifecycleHandler).Should().Be(1,
            "whoever adds a lifecycle handler decides whether a flagged tenant reaches it: {0}",
            string.Join(", ", handlers.Select(h => h.GetType().Name)));
    }
}

public sealed class TenantLifecycleDispatcherTests
{
    private sealed class ThrowingHandler : ITenantLifecycleHandler
    {
        public ValueTask OnTenantCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");

        public ValueTask OnTenantSuspendedAsync(string tenantId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");

        public ValueTask OnTenantDeletedAsync(string tenantId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }

    private static (TenantLifecycleDispatcher Dispatcher, IRealtimeSyncService Sync) Create(ITenantIdRegistry registry,
        params ITenantLifecycleHandler[] handlers)
    {
        var sync = Substitute.For<IRealtimeSyncService>();
        var agents = Substitute.For<IAgentStore>();
        agents.ListPageAfterAsync(Arg.Any<TenantId>(), Arg.Any<EntityId?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Agent>>([IsolationKit.Agent("acme", "a1")]));
        var queues = Substitute.For<IQueueStore>();
        queues.ListPageAfterAsync(Arg.Any<TenantId>(), Arg.Any<EntityId?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Queue>>([]));
        var removal = new FlaggedTenantRealtimeRemoval(sync, agents, queues, NullLogger<FlaggedTenantRealtimeRemoval>.Instance);
        var dispatcher = new TenantLifecycleDispatcher(handlers, new TenantIdAudit(registry), removal,
            NullLogger<TenantLifecycleDispatcher>.Instance);
        return (dispatcher, sync);
    }

    [Fact]
    public async Task SuspendedAsync_ShouldRunTheNextHandler_WhenOneHandlerThrows()
    {
        var recorder = new RecordingExternalHandler();
        var (dispatcher, _) = Create(new FixedTenantIdRegistry(IsolationKit.Id("globex")), new ThrowingHandler(), recorder);

        await dispatcher.SuspendedAsync("globex", default);

        recorder.Calls.Should().Equal("suspended:globex");
    }

    [Fact]
    public async Task SuspendedAsync_ShouldTakeTheFlaggedPath_WhenTheAuditCannotBeRead()
    {
        var recorder = new RecordingExternalHandler();
        var (dispatcher, sync) = Create(new UnreadableTenantIdRegistry(), recorder);

        await dispatcher.SuspendedAsync("acme", default);

        await sync.Received(1).RemoveAgentAsync("acme", "a1", Arg.Any<CancellationToken>());
        recorder.Calls.Should().BeEmpty("an unreadable audit is fail-closed");
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveAMembershipTheEffectiveViewLeavesOut_WhenTheQueueIsBeyondTheFirstPage()
    {
        var sync = Substitute.For<IRealtimeSyncService>();
        var agents = Substitute.For<IAgentStore>();
        agents.ListPageAfterAsync(Arg.Any<TenantId>(), Arg.Any<EntityId?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Agent>>([IsolationKit.Agent("acme", "a2", ext: null, pw: null)]));
        var firstPage = Enumerable.Range(1, 1000).Select(i => IsolationKit.Queue("acme", $"q{i:D4}", id: $"id{i:D4}")).ToList();
        var queues = Substitute.For<IQueueStore>();
        queues.ListPageAfterAsync(Arg.Any<TenantId>(), null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Queue>>(firstPage));
        queues.ListPageAfterAsync(Arg.Any<TenantId>(), EntityId.From("id1000"), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Queue>>([IsolationKit.Queue("acme", "q1001", id: "id1001")]));
        var removal = new FlaggedTenantRealtimeRemoval(sync, agents, queues, NullLogger<FlaggedTenantRealtimeRemoval>.Instance);

        await removal.RemoveAsync("acme", TenantIdFlagReason.PrefixPair, default);

        await sync.Received(1).RemoveQueueMemberAsync("acme", "q1001", "a2", Arg.Any<CancellationToken>());
        await sync.DidNotReceive().RemoveQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

public sealed class QueueDeleteFenceTests
{
    private const string Tenant = AuthenticatedPlatformApiFactory.TestTenantId;

    private static async Task<(WebApplicationFactory<Program> Factory, IRealtimeSyncService Sync)> CreateAsync(
        bool flagged, bool unreadable = false)
    {
        WebApplicationFactory<Program> factory = new AuthenticatedPlatformApiFactory();
        if (unreadable)
            factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(
                s => s.Replace(ServiceDescriptor.Singleton<ITenantIdRegistry, UnreadableTenantIdRegistry>())));
        _ = factory.Server;
        if (flagged)
            await IsolationKit.SeedTenantAsync(factory.Services, Tenant + "-corp");
        return (factory, factory.Services.GetRequiredService<IRealtimeSyncService>());
    }

    private static async Task DeleteQueueAsync(WebApplicationFactory<Program> factory, string name)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        using var created = await client.PostAsJsonAsync("/api/v1/admin/queues", new { name });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var id = JsonNode.Parse(body)!["id"]!.GetValue<string>();
        using var deleted = await client.DeleteAsync(new Uri($"/api/v1/admin/queues/{id}", UriKind.Relative));
        deleted.IsSuccessStatusCode.Should().BeTrue(await deleted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeleteQueue_ShouldKeepTheRealtimeQueueRow_WhenTheTenantIsFlagged()
    {
        var (factory, sync) = await CreateAsync(flagged: true);
        using (factory)
        {
            await DeleteQueueAsync(factory, "corp-x");

            await sync.DidNotReceive().RemoveQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            (await factory.Services.GetRequiredService<IQueueStore>().GetByNameAsync(new TenantId(Tenant), "corp-x", default))
                .Should().BeNull("the Platform record is deleted all the same");
        }
    }

    [Fact]
    public async Task DeleteQueue_ShouldRemoveTheRealtimeQueueRow_WhenTheTenantIsNotFlagged()
    {
        var (factory, sync) = await CreateAsync(flagged: false);
        using (factory)
        {
            await DeleteQueueAsync(factory, "support");

            await sync.Received(1).RemoveQueueAsync(Tenant, "support", Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task DeleteQueue_ShouldKeepTheRealtimeQueueRow_WhenTheAuditCannotBeRead()
    {
        var (factory, sync) = await CreateAsync(flagged: false, unreadable: true);
        using (factory)
        {
            await DeleteQueueAsync(factory, "support");

            await sync.DidNotReceive().RemoveQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }
}
