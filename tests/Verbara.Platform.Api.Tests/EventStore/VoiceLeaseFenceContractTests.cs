using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Cluster.Postgres.DependencyInjection;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Verbara.Sdk.Cluster.Primitives;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.EventStore.Postgres;
using Verbara.Sdk.Pro.EventStore.Postgres.DependencyInjection;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-are-written-once task B.3c, the host↔Pro contract (round 10 F2): the <c>2.26.0-pro</c> writer
/// (<see cref="PostgresSessionEventStore"/>) running Platform's real fence (<see cref="VoiceLeaseFence"/>) on one
/// database that holds the Sdk's <c>cluster_distributed_lock</c>. For each of the four lease-row states the writer
/// observes the matching verdict, the row's <c>expires_at</c> and a statement clock inside its transaction, and rolls
/// back every verdict but <see cref="FenceVerdict.Held"/>; a refused fence takes no lock on another owner's row.
/// </summary>
public sealed class VoiceLeaseFenceContractTests : IAsyncLifetime
{
    private const string Self = "replica-a";
    private const string Other = "replica-b";

    private IContainer? _postgres;
    private NpgsqlDataSource? _dataSource;

    public enum RowState
    {
        Held,
        NotOwner,
        Expired,
        NoRow,
    }

    [Theory]
    [InlineData(RowState.Held, FenceVerdict.Held)]
    [InlineData(RowState.NotOwner, FenceVerdict.NotOwner)]
    [InlineData(RowState.Expired, FenceVerdict.Expired)]
    [InlineData(RowState.NoRow, FenceVerdict.NoRow)]
    public async Task ObserveFenceAsync_ShouldReportTheRowsVerdictExpiryAndStatementClock_ForEachLeaseRowState(
        RowState state, FenceVerdict expected)
    {
        var resource = await SeedLeaseAsync(state);
        var store = Writer(resource);

        var before = await DatabaseClockAsync();
        var observation = await store.ObserveFenceAsync(CancellationToken.None);
        var after = await DatabaseClockAsync();

        observation.Verdict.Should().Be(expected);
        observation.ExpiresAt.Should().Be(await RowExpiryAsync(resource),
            "ExpiresAt is the lease row's expires_at, and null when there is no row");
        observation.ObservedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "ObservedAt is the statement clock of the fence inside the writer's own transaction");
    }

    [Theory]
    [InlineData(RowState.Held, true)]
    [InlineData(RowState.NotOwner, false)]
    [InlineData(RowState.Expired, false)]
    [InlineData(RowState.NoRow, false)]
    public async Task AppendFencedAsync_ShouldCommitOnlyUnderHeld_AndRollBackEveryOtherVerdict(
        RowState state, bool commits)
    {
        var resource = await SeedLeaseAsync(state);
        var store = Writer(resource);
        var linkedId = $"link-{Guid.NewGuid():N}";
        var before = await CountEventsAsync(linkedId);

        var result = await store.AppendFencedAsync(StartOf(linkedId), CancellationToken.None);

        result.Fence.Should().NotBeNull();
        if (commits)
        {
            result.Outcome.Should().NotBe(CallEventAppendOutcome.Refused);
            (await CountEventsAsync(linkedId)).Should().Be(before + 1, "a held fence lets the append commit");
        }
        else
        {
            result.Outcome.Should().Be(CallEventAppendOutcome.Refused);
            (await CountEventsAsync(linkedId)).Should().Be(before, "a refused fence rolls the append back before any write");
        }
    }

    [Fact]
    public async Task Fence_ShouldTakeNoLockOnAnotherOwnersRow_WhenItRefusesInsideAnOpenTransaction()
    {
        var resource = await SeedLeaseAsync(RowState.NotOwner);
        var fence = VoiceLeaseFence.Create(resource, Self);
        var distributedLock = DistributedLock();

        await using var connection = await _dataSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var observation = await fence(connection, transaction, CancellationToken.None);
        observation.Verdict.Should().Be(FenceVerdict.NotOwner);

        // Platform's refused transaction stays open while the real owner renews.
        using var renewal = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var renewed = await distributedLock.TryAcquireAsync(resource, Other, TimeSpan.FromSeconds(30), renewal.Token);

        renewed.Should().BeTrue("the owner's renewal commits: a refusing fence locks no other owner's row");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Fence_ShouldBlockItsOwnRowsRenewal_WhileItsHeldTransactionIsOpen()
    {
        // The control of the test above: the same open transaction under Held does hold the row lock, so the measurement
        // can see a blocked renewal.
        var resource = await SeedLeaseAsync(RowState.Held);
        var fence = VoiceLeaseFence.Create(resource, Self);
        var distributedLock = DistributedLock();

        await using var connection = await _dataSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        (await fence(connection, transaction, CancellationToken.None)).Verdict.Should().Be(FenceVerdict.Held);

        using var renewal = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var act = async () => await distributedLock.TryAcquireAsync(resource, Self, TimeSpan.FromSeconds(30), renewal.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the held fence's FOR NO KEY UPDATE lock queues the renewal until the transaction ends");
        await transaction.RollbackAsync();
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:18-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(
            $"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
            "Database=postgres;Username=postgres;Password=postgres");
        await MigrationRunner.EnsureSchemaAsync(_dataSource, NullLogger.Instance);
        new ServiceCollection().UsePostgresEventStore(_dataSource);   // the event store's schema migrator
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private PostgresSessionEventStore Writer(string resource)
    {
        var fence = VoiceLeaseFence.Create(resource, Self);
        return new PostgresSessionEventStore(
            new EventStoreDbContext(_dataSource!), NullLogger<PostgresSessionEventStore>.Instance, () => fence, null);
    }

    private IDistributedLock DistributedLock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddKeyedSingleton<NpgsqlDataSource>("Cluster", (_, _) => _dataSource!);
        services.AddPostgresDistributedLock(connectionStringName: "Cluster");
        return services.BuildServiceProvider().GetRequiredService<IDistributedLock>();
    }

    private async Task<string> SeedLeaseAsync(RowState state)
    {
        var resource = $"voice:ami:owner:leader:{Guid.NewGuid():N}";
        if (state == RowState.NoRow)
            return resource;

        var (owner, lifetime) = state switch
        {
            RowState.Held => (Self, "30 seconds"),
            RowState.NotOwner => (Other, "30 seconds"),
            _ => (Self, "-5 seconds"),
        };
        await using var command = _dataSource!.CreateCommand(
            $"INSERT INTO cluster_distributed_lock (resource, owner, expires_at) VALUES (@r, @o, now() + interval '{lifetime}')");
        command.Parameters.AddWithValue("r", resource);
        command.Parameters.AddWithValue("o", owner);
        await command.ExecuteNonQueryAsync();
        return resource;
    }

    private async Task<DateTimeOffset?> RowExpiryAsync(string resource)
    {
        await using var command = _dataSource!.CreateCommand(
            "SELECT expires_at FROM cluster_distributed_lock WHERE resource = @r");
        command.Parameters.AddWithValue("r", resource);
        var value = await command.ExecuteScalarAsync();
        return value is DateTime expiresAt ? new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)) : null;
    }

    private async Task<DateTimeOffset> DatabaseClockAsync()
    {
        await using var command = _dataSource!.CreateCommand("SELECT clock_timestamp()");
        var value = (DateTime)(await command.ExecuteScalarAsync())!;
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private async Task<long> CountEventsAsync(string linkedId)
    {
        await using var command = _dataSource!.CreateCommand("SELECT count(*) FROM session_events WHERE linked_id = @l");
        command.Parameters.AddWithValue("l", linkedId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static CallEventAppend StartOf(string linkedId) => new()
    {
        Row = new SessionEventRow
        {
            TenantId = "tenant-fence",
            SessionId = $"session-{linkedId}",
            SequenceNumber = 0,
            EventType = "CallStarted",
            ServerId = "srv-fence",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "{}",
        },
        LinkedId = linkedId,
        CallIndex = 1,
        EventIdentity = "1:CallStarted",
        Kind = CallEventAppendKind.Start,
    };
}
