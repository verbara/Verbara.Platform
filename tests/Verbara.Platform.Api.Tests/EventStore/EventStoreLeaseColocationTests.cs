using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.EventStore.Postgres;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>call-records-are-written-once task B.3c on PostgreSQL 17.</summary>
public sealed class EventStoreLeaseColocationPg17Tests() : EventStoreLeaseColocationTests("postgres:17-alpine");

/// <summary>call-records-are-written-once task B.3c on PostgreSQL 18.</summary>
public sealed class EventStoreLeaseColocationPg18Tests() : EventStoreLeaseColocationTests("postgres:18-alpine");

/// <summary>
/// call-records-are-written-once (design D13a; task B.3c): the Api refuses to start when clustered voice runs its voice
/// leader lease and its event store on connection strings the startup round trip cannot prove are one writable
/// database with the fence's rights, and starts when they are, or when the probe has nothing to decide. Each case boots
/// the real <c>Program</c> composition against the containers (round 3 F2): a primary with a <c>pg_basebackup</c>
/// physical standby run inside its container, a second cluster, and a transaction-mode PgBouncer in front of the
/// primary. Every case works in a database of its own.
/// </summary>
public abstract class EventStoreLeaseColocationTests(string image) : IAsyncLifetime
{
    private const string Password = "colocation-pw";
    private const string StandbyData = "/tmp/standby";
    private const int StandbyPort = 5433;

    private IContainer? _primary;
    private IContainer? _second;
    private IContainer? _bouncer;

    [Fact]
    public async Task Boot_ShouldRefuseAtTheEventStoresRead_WhenAnalyticsIsAnotherDatabase()
    {
        var lease = await CreateDatabaseAsync();
        var other = await CreateDatabaseAsync(withLockTable: true);

        var refusal = Refusal(Boot(Voice(cluster: Primary(lease), analytics: Primary(other))));

        refusal.Step.Should().StartWith("2 lock");
        refusal.Message.Should().Contain("0 rows")
            .And.Contain($"'{lease}'").And.Contain($"'{other}'")
            .And.Contain("from ConnectionStrings:Cluster").And.Contain("from ConnectionStrings:Analytics")
            .And.NotContain(Password);
        (await TableExistsAsync(Primary(other), "_schema_eventstore")).Should().BeFalse(
            "the probe refuses before the event store's migrator runs");
    }

    [Fact]
    public async Task Boot_ShouldRefuseNamingDialer_WhenDialerAloneIsAnotherDatabase()
    {
        var lease = await CreateDatabaseAsync();
        var other = await CreateDatabaseAsync(withLockTable: true);

        var refusal = Refusal(Boot(Voice(cluster: Primary(lease), dialer: Primary(other))));

        refusal.Message.Should().Contain("from ConnectionStrings:Dialer (fallback for Analytics)");
        (await TableExistsAsync(Primary(other), "_schema_dialer")).Should().BeFalse("the dialer's migrator never ran");
    }

    [Fact]
    public async Task Boot_ShouldRefuse_WhenTheEventStoreIsOnASecondCluster()
    {
        var lease = await CreateDatabaseAsync();
        await EnsureLockTableAsync(Second("postgres"));

        var refusal = Refusal(Boot(Voice(cluster: Primary(lease), analytics: Second("postgres"))));

        refusal.Step.Should().StartWith("2 lock");
        refusal.Message.Should().Contain("0 rows");
    }

    [Fact]
    public async Task Boot_ShouldRefuseWith42P01_WhenTheEventStoresDatabaseHasNoLockTable()
    {
        var lease = await CreateDatabaseAsync();
        var other = await CreateDatabaseAsync();

        var refusal = Refusal(Boot(Voice(cluster: Primary(lease), analytics: Primary(other))));

        refusal.SqlState.Should().Be(PostgresErrorCodes.UndefinedTable);
        refusal.Step.Should().StartWith("2 lock");
    }

    [Fact]
    public async Task Boot_ShouldRefuseAtTheLockingSelectWith25006_WhenTheEventStoreIsAPhysicalStandby()
    {
        var database = await CreateDatabaseAsync();
        await WaitForStandbyAsync(database);

        var refusal = Refusal(Boot(Voice(cluster: Primary(database), analytics: Standby(database))));

        refusal.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        refusal.Step.Should().StartWith("2 lock the probe row with SELECT ... FOR NO KEY UPDATE");
        (await TableExistsAsync(Primary(database), "_schema_eventstore")).Should().BeFalse(
            "the event store's migrator never ran");
        (await ProbeRowsAsync(Primary(database))).Should().Be(0, "a refused probe deletes its row");
    }

    [Fact]
    public void Boot_ShouldRefuseWithTheProbesDiagnostic_WhenTheEventStoresDatabaseIsUnreachable()
    {
        var refusal = Refusal(Boot(Voice(cluster: Primary("postgres"), analytics: Unreachable())));

        refusal.Message.Should().Contain("unreachable or timed out").And.Contain("from ConnectionStrings:Analytics");
        refusal.Step.Should().StartWith("2 open");
    }

    [Fact]
    public async Task Boot_ShouldRefuseWith42501AndTheGrantHint_WhenTheEventStoresRoleCannotUpdateTheLockTable()
    {
        var database = await CreateDatabaseAsync(withLockTable: true);
        var role = $"reader_{Guid.NewGuid():N}"[..20];
        await ExecuteAsync(Primary(database),
            $"CREATE ROLE {role} LOGIN PASSWORD '{Password}'; GRANT SELECT ON cluster_distributed_lock TO {role};");

        var refusal = Refusal(Boot(Voice(cluster: Primary(database), analytics: Primary(database, role))));

        refusal.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        refusal.Message.Should().Contain("Grant the event store's role SELECT and UPDATE on cluster_distributed_lock")
            .And.Contain("use the lease's role");
    }

    [Fact]
    public async Task Boot_ShouldStartAndLeaveNoProbeRow_WhenTheSameDatabaseIsReachedThroughPgBouncerAndDirectly()
    {
        var database = await CreateDatabaseAsync();
        using var probe = new ProbeQueries(database);

        var boot = Boot(Voice(cluster: Primary(database), analytics: Bouncer(database)));

        boot.Should().BeNull("the pooler and the direct host reach one database");
        (await ProbeRowsAsync(Primary(database))).Should().Be(0);
        probe.Count.Should().BeGreaterThan(0, "the strings differ, so the probe ran (the control of the 0-query cases)");
    }

    [Fact]
    public async Task Boot_ShouldStartWithZeroProbeQueries_WhenTheResolvedStringsAreEqual()
    {
        var database = await CreateDatabaseAsync();
        using var probe = new ProbeQueries(database);

        var boot = Boot(Voice(cluster: Primary(database), analytics: Primary(database)));

        boot.Should().BeNull();
        probe.Count.Should().Be(0, "equal strings are one data source: the probe does not run");
    }

    [Fact]
    public async Task Boot_ShouldStartWithNoProbe_WhenNoAmiIsConfiguredOverASplit()
    {
        var lease = await CreateDatabaseAsync();
        var other = await CreateDatabaseAsync();
        using var probe = new ProbeQueries(other);

        var boot = Boot(Settings(cluster: Primary(lease), analytics: Primary(other)));

        boot.Should().BeNull("with no AMI there is no voice leader lease and nothing to fence");
        probe.Count.Should().Be(0);
    }

    [Fact]
    public async Task Boot_ShouldDeleteThisInstancesExpiredLeftoverProbeRow_AndKeepEveryOtherRow()
    {
        const string instance = "colocation-instance";
        var database = await CreateDatabaseAsync(withLockTable: true);
        await ExecuteAsync(Primary(database), $"""
            INSERT INTO cluster_distributed_lock (resource, owner, expires_at) VALUES
              ('{EventStoreLeaseColocation.ProbeResourcePrefix}{instance}:crashed', '{instance}', now() - interval '1 minute'),
              ('{EventStoreLeaseColocation.ProbeResourcePrefix}{instance}:running', '{instance}', now() + interval '1 minute'),
              ('{EventStoreLeaseColocation.ProbeResourcePrefix}other-instance:crashed', 'other-instance', now() - interval '1 minute');
            """);
        var settings = Voice(cluster: Primary(database), analytics: Primary(database, applicationName: "events"));
        settings["Cluster:Leadership:InstanceId"] = instance;

        Boot(settings).Should().BeNull();

        (await ProbeResourcesAsync(Primary(database))).Should().BeEquivalentTo(
            $"{EventStoreLeaseColocation.ProbeResourcePrefix}{instance}:running",
            $"{EventStoreLeaseColocation.ProbeResourcePrefix}other-instance:crashed");
    }

    [Fact]
    public async Task Boot_ShouldRegisterNoFenceAndWriteNoCallEvents_OnAnAriOnlyHost()
    {
        var database = await CreateDatabaseAsync();
        var settings = Settings(cluster: Primary(database), analytics: Primary(database, applicationName: "events"));
        settings["Asterisk:Ari:BaseUrl"] = "http://asterisk.colocation-probe.invalid:8088";

        using var host = new ColocationProbeHostFactory(settings);
        host.Boot().Should().BeNull();

        host.Services.GetService<EventStoreFence>().Should().BeNull("an ARI-only host has no AMI owner lease to fence on");
        host.Services.GetKeyedService<IClusterLeader>(VoiceLeaderResources.AmiOwner).Should().BeNull();
        host.Services.GetKeyedService<IClusterLeader>(VoiceLeaderResources.Inbound).Should().NotBeNull();
        var subscriber = host.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriber.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await subscriber.StopAsync(CancellationToken.None);
        (await CountAsync(Primary(database), "session_events")).Should().Be(0,
            "no AMI server feeds the session manager, so there is no call event to write");
    }

    [Fact]
    public async Task Boot_ShouldRegisterTheFenceWithTheLeaseRenewalInterval_WhenAmiIsConfigured()
    {
        var database = await CreateDatabaseAsync();
        var settings = Voice(cluster: Primary(database), analytics: Primary(database));
        settings["Cluster:Leadership:RenewalInterval"] = "00:00:07";

        using var host = new ColocationProbeHostFactory(settings);
        host.Boot().Should().BeNull();

        host.Services.GetService<EventStoreFence>().Should().NotBeNull("clustered voice fences every event-store write (D13)");
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EventStoreOptions>>().Value;
        options.FenceRenewalInterval.Should().Be(TimeSpan.FromSeconds(7),
            "D13b's persistence threshold is the lease's renewal interval, ClusterLeadershipOptions.RenewalInterval");
        options.FenceResource.Should().Be(VoiceLeaderResources.AmiOwner);
    }

    [Fact]
    public async Task Boot_ShouldRefuseThroughDialerNamingIt_WhenDialerIsAStandbyAndAnalyticsIsUnset()
    {
        var database = await CreateDatabaseAsync();
        await WaitForStandbyAsync(database);

        var refusal = Refusal(Boot(Voice(cluster: Primary(database), dialer: Standby(database))));

        refusal.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        refusal.Message.Should().Contain("from ConnectionStrings:Dialer (fallback for Analytics)");
        (await TableExistsAsync(Primary(database), "_schema_dialer")).Should().BeFalse("the dialer's migrator never ran");
    }

    [Fact]
    public void Boot_ShouldRefuseThroughDialerNamingIt_WhenDialerIsUnreachableAndAnalyticsIsUnset()
    {
        var refusal = Refusal(Boot(Voice(cluster: Primary("postgres"), dialer: Unreachable())));

        refusal.Message.Should().Contain("unreachable or timed out")
            .And.Contain("from ConnectionStrings:Dialer (fallback for Analytics)");
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        _primary = Postgres(exposeStandby: true);
        _second = Postgres(exposeStandby: false);
        await Task.WhenAll(_primary.StartAsync(), _second.StartAsync());

        // On the default bridge, by the primary's address: no test network (the host's address pools are finite).
        _bouncer = new ContainerBuilder("edoburu/pgbouncer:latest")
            .WithEnvironment("DB_HOST", _primary.IpAddress)
            .WithEnvironment("DB_USER", "postgres")
            .WithEnvironment("DB_PASSWORD", Password)
            .WithEnvironment("AUTH_TYPE", "scram-sha-256")
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("MAX_CLIENT_CONN", "200")
            .WithEnvironment("DEFAULT_POOL_SIZE", "20")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
            .Build();
        await _bouncer.StartAsync();

        // The physical standby: pg_basebackup of the primary inside its own container, streaming on its own port.
        await ExecInPrimaryAsync(
            $"gosu postgres pg_basebackup -h 127.0.0.1 -U postgres -D {StandbyData} -R -X stream -c fast && " +
            $"chmod 700 {StandbyData} && " +
            $"gosu postgres pg_ctl -D {StandbyData} -o '-p {StandbyPort} -c hot_standby=on' -l /tmp/standby.log -w start");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        foreach (var container in new[] { _bouncer, _second, _primary })
        {
            if (container is not null)
                await container.DisposeAsync();
        }
    }

    private IContainer Postgres(bool exposeStandby)
    {
        var builder = new ContainerBuilder(image)
            .WithEnvironment("POSTGRES_PASSWORD", Password)
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"));
        if (exposeStandby)
            builder = builder.WithPortBinding(StandbyPort, true);
        return builder.Build();
    }

    private async Task ExecInPrimaryAsync(string script)
    {
        var result = await _primary!.ExecAsync(["sh", "-c", script]);
        result.ExitCode.Should().Be(0, $"the container step failed: {result.Stdout}{result.Stderr}");
    }

    private static Exception? Boot(Dictionary<string, string?> settings)
    {
        using var host = new ColocationProbeHostFactory(settings);
        return host.Boot();
    }

    private static EventStoreLeaseColocationException Refusal(Exception? boot)
    {
        boot.Should().NotBeNull("the Api must refuse to start");
        for (var current = boot; current is not null; current = current.InnerException)
        {
            if (current is EventStoreLeaseColocationException refusal)
                return refusal;
            if (current is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.OfType<EventStoreLeaseColocationException>().FirstOrDefault() is { } inner)
                return inner;
        }

        throw new Xunit.Sdk.XunitException($"The Api did not refuse with the probe's diagnostic; it threw:{Environment.NewLine}{boot}");
    }

    private static Dictionary<string, string?> Voice(string cluster, string? analytics = null, string? dialer = null)
    {
        var settings = Settings(cluster, analytics, dialer);
        settings["Asterisk:Ami:Hostname"] = ColocationProbeHostFactory.AmiHostname;
        settings["Asterisk:Ami:Port"] = "5038";
        settings["Asterisk:Ami:Username"] = "colocation";
        settings["Asterisk:Ami:Password"] = "colocation";
        return settings;
    }

    private static Dictionary<string, string?> Settings(string cluster, string? analytics = null, string? dialer = null) =>
        new()
        {
            ["ConnectionStrings:Cluster"] = cluster,
            ["ConnectionStrings:Analytics"] = analytics,
            ["ConnectionStrings:Dialer"] = dialer,
        };

    private string Primary(string database, string user = "postgres", string applicationName = "lease") =>
        Connection(_primary!.Hostname, _primary.GetMappedPublicPort(5432), database, user, applicationName);

    private string Standby(string database) =>
        Connection(_primary!.Hostname, _primary.GetMappedPublicPort(StandbyPort), database, "postgres", "standby");

    private string Second(string database) =>
        Connection(_second!.Hostname, _second.GetMappedPublicPort(5432), database, "postgres", "second");

    private string Bouncer(string database) =>
        Connection(_bouncer!.Hostname, _bouncer.GetMappedPublicPort(5432), database, "postgres", "bouncer");

    private static string Unreachable() =>
        "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Password=" + Password + ";Timeout=3";

    private static string Connection(string host, int port, string database, string user, string applicationName) =>
        $"Host={host};Port={port};Database={database};Username={user};Password={Password};Application Name={applicationName}";

    private async Task<string> CreateDatabaseAsync(bool withLockTable = false)
    {
        var database = $"coloc_{Guid.NewGuid():N}"[..24];
        await ExecuteAsync(Primary("postgres"), $"CREATE DATABASE {database}");
        if (withLockTable)
            await EnsureLockTableAsync(Primary(database));
        return database;
    }

    private static async Task EnsureLockTableAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await MigrationRunner.EnsureSchemaAsync(dataSource, NullLogger.Instance);
    }

    private async Task WaitForStandbyAsync(string database)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(Standby(database));
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection);
        command.Parameters.AddWithValue("t", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        if (!await TableExistsAsync(connectionString, table))
            return 0;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ProbeRowsAsync(string connectionString) =>
        (await ProbeResourcesAsync(connectionString)).Count;

    private static async Task<IReadOnlyList<string>> ProbeResourcesAsync(string connectionString)
    {
        if (!await TableExistsAsync(connectionString, "cluster_distributed_lock"))
            return [];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT resource FROM cluster_distributed_lock WHERE starts_with(resource, @p) ORDER BY resource", connection);
        command.Parameters.AddWithValue("p", EventStoreLeaseColocation.ProbeResourcePrefix);
        var resources = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            resources.Add(reader.GetString(0));
        return resources;
    }

    /// <summary>
    /// Counts the Npgsql commands the startup probe issues against one database: an activity of the <c>Npgsql</c> source
    /// whose data source is one of the probe's two and whose database is <paramref name="database"/>.
    /// </summary>
    private sealed class ProbeQueries : IDisposable
    {
        private readonly ActivityListener _listener;
        private int _count;

        public ProbeQueries(string database)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    var dataSource = activity.GetTagItem("db.npgsql.data_source") as string;
                    if (dataSource is EventStoreLeaseColocation.LeaseDataSourceName
                            or EventStoreLeaseColocation.EventStoreDataSourceName
                        && activity.GetTagItem("db.namespace") as string == database)
                    {
                        Interlocked.Increment(ref _count);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public int Count => Volatile.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
