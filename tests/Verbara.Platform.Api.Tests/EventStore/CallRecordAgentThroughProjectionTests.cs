using System.Net;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-name-the-queue-agent (design D4; task 1.3): an answered queue call's record names the Platform agent
/// whose member interface took it. The real Api <c>Program</c> as the event store's writer, with its Postgres event store
/// and Pro's projector over a Testcontainers Postgres, observes two queue calls: one taken by a Platform agent's member
/// interface (<c>PJSIP/{tenant}-agent-{id}</c>) with no agent id from the Sdk, and one taken by a <c>Local</c> member
/// whose agent the Sdk names. The stored records and the CDR list's <c>?agent=</c> filter read what the projection wrote.
/// </summary>
public sealed class CallRecordAgentThroughProjectionTests : IAsyncLifetime
{
    private const string TenantId = "tenant-queue-agent";
    private const string ServerId = "srv-queue-agent";
    private const string QueueName = "sales";
    private const string PlatformAgentId = "9a0abfcbd0884569840553dcbf342759";
    private const string SdkAgentId = "1001";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow.AddMinutes(-1);

    private IContainer? _postgres;

    [Fact]
    public async Task CallRecord_ShouldNameThePlatformAgent_WhenAQueueCallIsAnsweredByItsMemberInterface()
    {
        using var source = new Subject<SessionDomainEvent>();
        var platformMember = new CallSession(Guid.NewGuid().ToString("N"), "1762430400.501", ServerId, CallDirection.Inbound)
        {
            TenantId = TenantId,
            QueueName = QueueName,
            AgentInterface = $"PJSIP/{TenantId}-agent-{PlatformAgentId}",
        };
        var localMember = new CallSession(Guid.NewGuid().ToString("N"), "1762430400.502", ServerId, CallDirection.Inbound)
        {
            TenantId = TenantId,
            QueueName = QueueName,
            AgentId = SdkAgentId,
            AgentInterface = $"Local/{SdkAgentId}@from-queue/n",
        };
        var sessionManager = Substitute.For<ICallSessionManager>();
        sessionManager.Events.Returns(source);
        foreach (var session in new[] { platformMember, localMember })
        {
            sessionManager.GetById(session.SessionId).Returns(session);
            sessionManager.GetByLinkedId(session.LinkedId).Returns(session);
        }

        using var host = new EventStoreReplicaFactory(
            ConnectionString("analytics"), ConnectionString("cluster"), sessionManager,
            new FakeVoiceLeader("replica-queue-agent", isLeader: false), postgresEventStore: true)
        {
            ApiKeyTenantId = TenantId,
        };
        await HoldVoiceLeaseAsync(host);
        var subscriber = host.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriber.StartAsync(CancellationToken.None);
        try
        {
            RaiseQueueCall(source, platformMember.SessionId, sdkAgentId: null, offsetSeconds: 0);
            RaiseQueueCall(source, localMember.SessionId, sdkAgentId: SdkAgentId, offsetSeconds: 1);

            var store = host.Services.GetRequiredService<ICompletedSessionStore>();
            var platformRecord = await WaitForRecordAsync(store, platformMember.SessionId);
            var localRecord = await WaitForRecordAsync(store, localMember.SessionId);

            localRecord.AgentId.Should().Be(SdkAgentId,
                "an interface outside Platform's scheme keeps the agent the Sdk recorded");
            platformRecord.AgentId.Should().Be(PlatformAgentId,
                "the record of a queue call taken by a Platform agent's member interface names that agent");

            using var client = host.CreateAuthenticatedClient();
            var list = await GetJsonAsync(client, $"/api/v1/analytics/cdr?agent={PlatformAgentId}");
            var sessionIds = list["data"]!.AsArray().Select(r => r!["sessionId"]!.GetValue<string>()).ToList();
            sessionIds.Should().Equal([platformMember.SessionId],
                "the CDR list's agent filter finds the queue call the Platform agent took, and only it");
        }
        finally
        {
            await subscriber.StopAsync(CancellationToken.None);
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

    /// <summary>
    /// Makes the replica the event store's writer: the lease row its fence reads, owned by its InstanceId, then its
    /// leader election's answer. The real election is a hosted service the test host strips.
    /// </summary>
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

    private static void RaiseQueueCall(
        Subject<SessionDomainEvent> source, string sessionId, string? sdkAgentId, int offsetSeconds)
    {
        var start = BaseTime.AddSeconds(offsetSeconds);
        source.OnNext(new CallStartedEvent(sessionId, ServerId, start, CallDirection.Inbound, "5551234"));
        source.OnNext(new CallConnectedEvent(
            sessionId, ServerId, start.AddSeconds(5), sdkAgentId, QueueName, TimeSpan.FromSeconds(5)));
        source.OnNext(new CallEndedEvent(
            sessionId, ServerId, start.AddSeconds(30), HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));
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

    private static async Task<JsonObject> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private string ConnectionString(string role) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name=eventstore-queue-agent-{role}";
}
