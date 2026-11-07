using System.Net;
using System.Reactive.Subjects;
using System.Reflection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-are-written-once (design D4, D5; task B.3b, round 2 F1 and round 12 F1): a replica that was the standby
/// while a call ran holds the call tenant-less, because only the voice leader stamps a tenant. When it acquires the voice
/// leader lease inside the recovery window, the event store asks the host's tenant resolver for the call's tenant by
/// LinkedId: the Conversation the previous leader created, else the live call's trunk leg (<c>TENANT_ID</c>). The real
/// Api <c>Program</c> with its Postgres event store, over a Testcontainers Postgres; the CDR endpoint reads the record.
/// </summary>
public sealed class CallTenantResolvedAtAcquisitionTests : IAsyncLifetime
{
    private const string TenantId = "tenant-acquired";
    private const string ServerId = "srv-acquired";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow.AddMinutes(-1);

    private IContainer? _postgres;

    [Fact]
    public async Task CallRecord_ShouldBeWrittenUnderTheConversationTenant_WhenTheCallEndedTenantlessBeforeTheStandbyAcquired()
    {
        using var source = new Subject<SessionDomainEvent>();
        var (sessionManager, session) = SessionSource(source, "1762430400.301");
        using var host = Replica(sessionManager);
        await SeedConversationAsync(host, session.LinkedId);
        var subscriber = host.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriber.StartAsync(CancellationToken.None);
        try
        {
            RaiseStartAndConnect(source, session);
            MarkEnded(session);
            RaiseEnd(source, session);

            await HoldVoiceLeaseAsync(host);

            using var client = host.CreateAuthenticatedClient();
            var status = await WaitForCdrAsync(client, session.SessionId);

            status.Should().Be(HttpStatusCode.OK,
                "the call's record is written under the tenant of the Conversation found by its LinkedId");
            (await CountAsync("completed_sessions")).Should().Be(1);
        }
        finally
        {
            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CallRecord_ShouldBeWrittenUnderTheTrunkLegTenant_WhenTheCallWasBornInTheGapAndIsUpAtAcquisition()
    {
        using var source = new Subject<SessionDomainEvent>();
        var (sessionManager, session) = SessionSource(source, "1762430400.302");
        AddCaller(session, "PJSIP/t-9-00000001");
        using var host = Replica(sessionManager);
        var ami = Substitute.For<IAmiConnection>();
#pragma warning disable CA2012 // ValueTask in NSubstitute setup
        ami.SendActionAsync<GetVarResponse>(
                Arg.Is<ManagerAction>(a => a is GetVarAction && ((GetVarAction)a).Variable == "TENANT_ID"),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<GetVarResponse>(new GetVarResponse { Response = "Success", Value = TenantId }));
#pragma warning restore CA2012
        host.Services.GetRequiredService<VerbaraServerPool>()
            .AddExistingServer("primary", new VerbaraServer(ami, NullLogger<VerbaraServer>.Instance));
        var subscriber = host.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriber.StartAsync(CancellationToken.None);
        try
        {
            RaiseStartAndConnect(source, session);

            await HoldVoiceLeaseAsync(host);
            await WaitUntilAsync(() => session.TenantId is not null);
            MarkEnded(session);
            RaiseEnd(source, session);

            using var client = host.CreateAuthenticatedClient();
            var status = await WaitForCdrAsync(client, session.SessionId);

            status.Should().Be(HttpStatusCode.OK,
                "a call with no Conversation that is still up at acquisition takes its tenant from its trunk leg");
            (await CountAsync("completed_sessions")).Should().Be(1, "the call has one record");
            var record = await host.Services.GetRequiredService<ICompletedSessionStore>()
                .GetAsync(TenantId, session.SessionId, CancellationToken.None);
            record!.IsPartial.Should().BeFalse("a call born in the gap is the new writer's own and its history is complete");
        }
        finally
        {
            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CallRecord_ShouldNotBeWritten_WhenTheCallHasNeitherAConversationNorALiveSession()
    {
        using var source = new Subject<SessionDomainEvent>();
        var (sessionManager, session) = SessionSource(source, "1762430400.303");
        AddCaller(session, "PJSIP/t-9-00000002");
        using var host = Replica(sessionManager);
        var subscriber = host.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriber.StartAsync(CancellationToken.None);
        try
        {
            RaiseStartAndConnect(source, session);
            MarkEnded(session);
            RaiseEnd(source, session);

            await HoldVoiceLeaseAsync(host);
            using var client = host.CreateAuthenticatedClient();
            var status = await WaitForCdrAsync(client, session.SessionId, Settle / 3);

            status.Should().Be(HttpStatusCode.NotFound);
            (await CountAsync("completed_sessions")).Should().Be(0, "with no tenant the call gets no record");
            (await CountAsync("session_events")).Should().Be(0, "and none of its events is written");
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

    /// <summary>The standby replica: the voice leader lease answers false until the test hands it over.</summary>
    private EventStoreReplicaFactory Replica(ICallSessionManager sessionManager) =>
        new(ConnectionString("analytics"), ConnectionString("cluster"), sessionManager,
            new FakeVoiceLeader("replica-b", isLeader: false), postgresEventStore: true)
        {
            ApiKeyTenantId = TenantId,
        };

    /// <summary>
    /// Hands the voice leader lease to the replica: the lease row its fence reads (D13), owned by its InstanceId, then its
    /// leader election's answer. The real election is a hosted service the test host strips.
    /// </summary>
    private async Task HoldVoiceLeaseAsync(EventStoreReplicaFactory host)
    {
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

    private static (ICallSessionManager Manager, CallSession Session) SessionSource(
        Subject<SessionDomainEvent> source, string linkedId)
    {
        var session = new CallSession(Guid.NewGuid().ToString("N"), linkedId, ServerId, CallDirection.Inbound);
        var manager = Substitute.For<ICallSessionManager>();
        manager.Events.Returns(source);
        manager.GetById(session.SessionId).Returns(session);
        manager.GetByLinkedId(linkedId).Returns(session);
        manager.ActiveSessions.Returns(_ => VoiceIsLive(session) ? [session] : []);
        return (manager, session);
    }

    private static bool VoiceIsLive(CallSession session) =>
        session.State is not (CallSessionState.Completed or CallSessionState.Failed);

    private static void RaiseStartAndConnect(Subject<SessionDomainEvent> source, CallSession session)
    {
        source.OnNext(new CallStartedEvent(session.SessionId, ServerId, BaseTime, CallDirection.Inbound, "5551234"));
        source.OnNext(new CallConnectedEvent(
            session.SessionId, ServerId, BaseTime.AddSeconds(5), "agent-1", "sales", TimeSpan.FromSeconds(5)));
    }

    private static void RaiseEnd(Subject<SessionDomainEvent> source, CallSession session) =>
        source.OnNext(new CallEndedEvent(
            session.SessionId, ServerId, BaseTime.AddSeconds(30), HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));

    // CallSession.State and AddParticipant are Sdk-internal: the session engine sets them; a test reaches them by reflection.
    private static void MarkEnded(CallSession session) =>
        typeof(CallSession).GetProperty(nameof(CallSession.State))!.SetValue(session, CallSessionState.Completed);

    private static void AddCaller(CallSession session, string channel)
    {
        var participant = new SessionParticipant
        {
            UniqueId = channel,
            Channel = channel,
            Technology = "PJSIP",
            Role = ParticipantRole.Caller,
        };
        var add = typeof(CallSession).GetMethod("AddParticipant", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? throw new InvalidOperationException("CallSession.AddParticipant not found.");
        add.Invoke(session, [participant]);
    }

    /// <summary>The Conversation the previous voice leader created for the call.</summary>
    private static async Task SeedConversationAsync(EventStoreReplicaFactory host, string linkedId)
    {
        var conversations = host.Services.GetRequiredService<IConversationStore>();
        await conversations.SaveAsync(new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(TenantId),
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            CreatedAt = BaseTime,
            VoiceLinkedId = linkedId,
        }, CancellationToken.None);
    }

    private static async Task<HttpStatusCode> WaitForCdrAsync(HttpClient client, string sessionId, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Settle);
        while (true)
        {
            using var response = await client.GetAsync($"/api/v1/analytics/cdr/{sessionId}");
            if (response.StatusCode != HttpStatusCode.NotFound || DateTimeOffset.UtcNow >= deadline)
                return response.StatusCode;
            await Task.Delay(100); // fence-allow: LOOP-DRIVER — poll pacing for the CDR read, bounded by the deadline
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Settle;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"The condition did not hold within {Settle}.");
            await Task.Delay(25); // fence-allow: LOOP-DRIVER — poll pacing for the condition, bounded by Settle
        }
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("count"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private string ConnectionString(string role) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name=eventstore-acquired-{role}";
}
