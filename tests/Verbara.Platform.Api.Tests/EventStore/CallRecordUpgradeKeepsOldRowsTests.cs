using System.Reactive.Subjects;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-name-the-queue-agent (spec "An upgraded deployment keeps its old rows", design D5; task 2.4): answered
/// queue call records written before the release that registers the call agent resolver keep no agent, and an upgrade
/// does not change them. One Testcontainers Postgres holds the whole deployment: a first real <c>Program</c> host runs
/// Pro's migrations, the test writes pre-upgrade rows, and a second host over the same database is the upgrade's start.
/// The upgrade note's count query (CHANGELOG <c>[Unreleased]</c>) is read over the seeded rows.
/// </summary>
public sealed class CallRecordUpgradeKeepsOldRowsTests : IAsyncLifetime
{
    /// <summary>The upgrade note's query, verbatim from the CHANGELOG.</summary>
    internal const string UpgradeNoteCountQuery = """
        SELECT count(*) FROM completed_sessions
        WHERE queue_name IS NOT NULL AND connected_at IS NOT NULL
          AND agent_id IS NULL AND linked_id IS NOT NULL;
        """;

    private const string TenantId = "tenant-upgrade";
    private const string ServerId = "srv-upgrade";
    private const string QueueName = "support";
    private const string PlatformAgentId = "5b6c7d8e9f001122334455667788aabb";
    private const int SeededQueueRows = 3;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow.AddMinutes(-1);

    private IContainer? _postgres;

    [Fact]
    public async Task Upgrade_ShouldKeepOldRowsWithoutAgentAndNameTheAgentOfNewCalls_WhenTheHostRestartsOverThem()
    {
        // The deployment before the upgrade: a host whose event store migrated the schema.
        using (var idle = new Subject<SessionDomainEvent>())
        using (var before = NewReplica(idle, Substitute.For<ICallSessionManager>(), "before"))
        {
            await HoldVoiceLeaseAsync(before);
            var subscriber = before.Services.GetRequiredService<EventStoreSubscriber>();
            await subscriber.StartAsync(CancellationToken.None);
            await subscriber.StopAsync(CancellationToken.None);
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString("seed"));
        var seeded = await SeedPreUpgradeRowsAsync(dataSource);
        var seededJson = await ReadRowsAsJsonAsync(dataSource, seeded);
        (await CountAsync(dataSource)).Should().Be(SeededQueueRows,
            "the upgrade note's query counts the answered queue rows written since V004 with no agent, not the control row");

        // The upgrade's start: a second host over the same database.
        using var source = new Subject<SessionDomainEvent>();
        var call = new CallSession(Guid.NewGuid().ToString("N"), "1762430400.601", ServerId, CallDirection.Inbound)
        {
            TenantId = TenantId,
            QueueName = QueueName,
            AgentInterface = $"PJSIP/{TenantId}-agent-{PlatformAgentId}",
        };
        var sessionManager = Substitute.For<ICallSessionManager>();
        sessionManager.Events.Returns(source);
        sessionManager.GetById(call.SessionId).Returns(call);
        sessionManager.GetByLinkedId(call.LinkedId).Returns(call);

        using var after = NewReplica(source, sessionManager, "after");
        await HoldVoiceLeaseAsync(after);
        var upgraded = after.Services.GetRequiredService<EventStoreSubscriber>();
        await upgraded.StartAsync(CancellationToken.None);
        try
        {
            (await CountAsync(dataSource)).Should().Be(SeededQueueRows,
                "the upgraded host's start changes no pre-upgrade row");

            var start = BaseTime;
            source.OnNext(new CallStartedEvent(call.SessionId, ServerId, start, CallDirection.Inbound, "5551234"));
            source.OnNext(new CallConnectedEvent(
                call.SessionId, ServerId, start.AddSeconds(5), null, QueueName, TimeSpan.FromSeconds(5)));
            source.OnNext(new CallEndedEvent(
                call.SessionId, ServerId, start.AddSeconds(30), HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));

            var store = after.Services.GetRequiredService<ICompletedSessionStore>();
            var record = await WaitForRecordAsync(store, call.SessionId);

            (await ReadRowsAsJsonAsync(dataSource, seeded)).Should().Equal(seededJson,
                "every pre-upgrade row is byte-equal after the upgrade: none is repaired");
            record.AgentId.Should().Be(PlatformAgentId, "a call projected after the upgrade's start names its agent");
            (await CountAsync(dataSource)).Should().Be(SeededQueueRows,
                "the upgrade note's query still counts only the pre-upgrade rows once new calls name their agent");
        }
        finally
        {
            await upgraded.StopAsync(CancellationToken.None);
        }
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private EventStoreReplicaFactory NewReplica(
        Subject<SessionDomainEvent> source, ICallSessionManager sessionManager, string name)
    {
        sessionManager.Events.Returns(source);
        return new EventStoreReplicaFactory(
            ConnectionString("analytics"), ConnectionString("cluster"), sessionManager,
            new FakeVoiceLeader($"replica-upgrade-{name}", isLeader: false), postgresEventStore: true)
        {
            ApiKeyTenantId = TenantId,
        };
    }

    /// <summary>
    /// Writes <see cref="SeededQueueRows"/> answered queue call records with no agent, as a host without the resolver
    /// wrote them (queue, connect time and linked id set), and one control row with no linked id (written before V004).
    /// </summary>
    private static async Task<IReadOnlyList<string>> SeedPreUpgradeRowsAsync(NpgsqlDataSource dataSource)
    {
        var sessionIds = new List<string>();
        for (var i = 0; i <= SeededQueueRows; i++)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var linkedId = i < SeededQueueRows ? $"1762430300.{i}" : null;
            await using var command = dataSource.CreateCommand("""
                INSERT INTO completed_sessions (
                    session_id, tenant_id, server_id, direction, agent_id, queue_name, started_at, connected_at,
                    completed_at, duration_ms, final_state, event_count, linked_id, call_index, agent_interface)
                VALUES (
                    @session_id, @tenant_id, @server_id, 0, NULL, @queue_name, @started_at, @connected_at,
                    @completed_at, 30000, 0, 3, @linked_id, @call_index, @agent_interface)
                """);
            var startedAt = BaseTime.AddMinutes(-10 - i);
            command.Parameters.AddWithValue("session_id", sessionId);
            command.Parameters.AddWithValue("tenant_id", TenantId);
            command.Parameters.AddWithValue("server_id", ServerId);
            command.Parameters.AddWithValue("queue_name", QueueName);
            command.Parameters.AddWithValue("started_at", startedAt);
            command.Parameters.AddWithValue("connected_at", startedAt.AddSeconds(5));
            command.Parameters.AddWithValue("completed_at", startedAt.AddSeconds(30));
            command.Parameters.Add(new NpgsqlParameter("linked_id", NpgsqlDbType.Text)
            {
                Value = (object?)linkedId ?? DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("call_index", NpgsqlDbType.Smallint)
            {
                Value = linkedId is null ? DBNull.Value : (short)0,
            });
            command.Parameters.Add(new NpgsqlParameter("agent_interface", NpgsqlDbType.Text)
            {
                Value = $"PJSIP/{TenantId}-agent-{PlatformAgentId}",
            });
            await command.ExecuteNonQueryAsync();
            sessionIds.Add(sessionId);
        }

        return sessionIds;
    }

    private static async Task<IReadOnlyList<string>> ReadRowsAsJsonAsync(
        NpgsqlDataSource dataSource, IReadOnlyList<string> sessionIds)
    {
        var rows = new List<string>();
        foreach (var sessionId in sessionIds)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT row_to_json(c)::text FROM completed_sessions c WHERE session_id = @session_id");
            command.Parameters.AddWithValue("session_id", sessionId);
            rows.Add((string)(await command.ExecuteScalarAsync())!);
        }

        return rows;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(UpgradeNoteCountQuery);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task HoldVoiceLeaseAsync(EventStoreReplicaFactory host)
    {
        _ = host.Services; // builds the host, so the cluster schema its registration migrates exists
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString("lease"));
        await Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync(
            dataSource, NullLogger.Instance, CancellationToken.None);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cluster_distributed_lock (resource, owner, expires_at)
            VALUES (@resource, @owner, now() + interval '5 minutes')
            ON CONFLICT (resource) DO UPDATE SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at
            """);
        command.Parameters.AddWithValue("resource", Verbara.Platform.Api.Services.VoiceLeaderResources.AmiOwner);
        command.Parameters.AddWithValue("owner", host.VoiceLeader!.InstanceId);
        await command.ExecuteNonQueryAsync();
        host.VoiceLeader.IsLeader = true;
    }

    private static async Task<CompletedSessionRow> WaitForRecordAsync(ICompletedSessionStore store, string sessionId)
    {
        var deadline = DateTimeOffset.UtcNow + Settle;
        while (true)
        {
            var row = await store.GetAsync(TenantId, sessionId, CancellationToken.None);
            if (row is not null)
                return row;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException($"The record of {sessionId} was not written within {Settle}.");
            await Task.Delay(100); // fence-allow: LOOP-DRIVER — poll pacing until the writer's projection commits the record
        }
    }

    private string ConnectionString(string role) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name=eventstore-upgrade-{role}";
}
