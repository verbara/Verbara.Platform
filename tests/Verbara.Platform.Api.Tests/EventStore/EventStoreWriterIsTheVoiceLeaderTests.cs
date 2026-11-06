using System.Reactive.Subjects;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-are-written-once (design D1; task B.1): the event store's writer is the replica that holds the voice
/// leader lease (<c>VoiceLeaderResources.AmiOwner</c>). Two replicas, each the real Api <c>Program</c>, observe one call
/// from one call session source, as every replica of a clustered deployment observes every call of the PBX they share;
/// only the replica whose voice leader answers true appends the call's events. Boots against a Testcontainers Postgres,
/// because the event store's Postgres registration and the cluster's run their migrations at registration.
/// </summary>
public sealed class EventStoreWriterIsTheVoiceLeaderTests : IAsyncLifetime
{
    private const string TenantId = "tenant-writer";
    private const string ServerId = "srv-writer";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private IContainer? _postgres;

    [Fact]
    public async Task CallEvents_ShouldBeAppendedOnlyByTheVoiceLeader_WhenTwoReplicasShareOneCallSessionSource()
    {
        using var source = new Subject<SessionDomainEvent>();
        var sessionManager = Substitute.For<ICallSessionManager>();
        sessionManager.Events.Returns(source);
        var sessionId = Guid.NewGuid().ToString("N");
        sessionManager.GetById(sessionId).Returns(
            new CallSession(sessionId, "1762430400.101", ServerId, CallDirection.Inbound) { TenantId = TenantId });

        using var hostA = new EventStoreReplicaFactory(
            ConnectionString("analytics-a"), ConnectionString("cluster-a"), sessionManager, new FakeVoiceLeader("replica-a", isLeader: true));
        using var hostB = new EventStoreReplicaFactory(
            ConnectionString("analytics-b"), ConnectionString("cluster-b"), sessionManager, new FakeVoiceLeader("replica-b", isLeader: false));
        var subscriberA = hostA.Services.GetRequiredService<EventStoreSubscriber>();
        var subscriberB = hostB.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriberA.StartAsync(CancellationToken.None);
        await subscriberB.StartAsync(CancellationToken.None);

        try
        {
            source.OnNext(new CallStartedEvent(sessionId, ServerId, BaseTime, CallDirection.Inbound, "5551234"));
            source.OnNext(new CallConnectedEvent(
                sessionId, ServerId, BaseTime.AddSeconds(5), "agent-1", "sales", TimeSpan.FromSeconds(5)));
            source.OnNext(new CallEndedEvent(
                sessionId, ServerId, BaseTime.AddSeconds(30), HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));

            // The control: the voice leader writes the call. B observes the same three events at the same time, so a B that
            // writes has written within the same settle.
            await WaitUntilAsync(() => hostA.Store.Rows.Count >= 3);
            await WaitUntilAsync(() => hostB.Store.Rows.Count >= 3, throwOnTimeout: false, Settle / 5);

            subscriberB.LastHeartbeatUtc.Should().NotBeNull("replica B observed the call, as every replica does");
            hostA.Store.Rows.Select(r => r.EventType).Should().Equal(
                ["CallStarted", "CallConnected", "CallEnded"], "the replica that holds the voice leader lease writes the call");
            hostB.Store.Rows.Select(r => r.EventType).Should().Equal(
                Array.Empty<string>(), "replica B does not hold the voice leader lease, so it is not the event store's writer");
        }
        finally
        {
            await subscriberA.StopAsync(CancellationToken.None);
            await subscriberB.StopAsync(CancellationToken.None);
        }
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only temporary
            // server, so a socket probe greens before 5432 accepts connections.
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

    private static async Task WaitUntilAsync(Func<bool> condition, bool throwOnTimeout = true, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Settle);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (throwOnTimeout)
                    throw new TimeoutException($"The condition did not hold within {within ?? Settle}.");
                return;
            }

            await Task.Delay(25);
        }
    }

    /// <summary>The container's connection string for one role, named in <c>Application Name</c>.</summary>
    private string ConnectionString(string role) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name=eventstore-writer-{role}";
}
