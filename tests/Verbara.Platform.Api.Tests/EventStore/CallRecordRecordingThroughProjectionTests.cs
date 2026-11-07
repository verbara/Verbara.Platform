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
/// call-records-are-written-once (design D3, audit F3; task B.4c, round 8 F1 and F2): whether a call record names a
/// recording is a fact about the call, decided at projection by the host's recording resolver over
/// <c>RecordingOptions.BasePath</c>, never by the naming pattern alone. The real Api <c>Program</c> as the event store's
/// writer, with its Postgres event store and Pro's projector over a Testcontainers Postgres, observes two calls: one whose
/// recording file is on disk and one with none. The CDR list, the CDR detail and the recording stream read the records
/// the projection wrote.
/// </summary>
public sealed class CallRecordRecordingThroughProjectionTests : IAsyncLifetime
{
    private const string TenantId = "tenant-recorded";
    private const string ServerId = "srv-recorded";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static readonly byte[] RecordingBytes = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45];

    private IContainer? _postgres;
    private string? _recordingsRoot;

    [Fact]
    public async Task CdrEndpoints_ShouldNameARecordingOnlyForTheRecordedCall_WhenTheProjectionWritesBothRecords()
    {
        using var source = new Subject<SessionDomainEvent>();
        var recorded = new CallSession(Guid.NewGuid().ToString("N"), "1762430400.401", ServerId, CallDirection.Inbound)
        {
            TenantId = TenantId,
        };
        var unrecorded = new CallSession(Guid.NewGuid().ToString("N"), "1762430400.402", ServerId, CallDirection.Inbound)
        {
            TenantId = TenantId,
        };
        var sessionManager = Substitute.For<ICallSessionManager>();
        sessionManager.Events.Returns(source);
        foreach (var session in new[] { recorded, unrecorded })
        {
            sessionManager.GetById(session.SessionId).Returns(session);
            sessionManager.GetByLinkedId(session.LinkedId).Returns(session);
        }

        // The recorded call's file, where Asterisk's MixMonitor writes it for the tenant, named by the record's session id.
        var tenantDir = Directory.CreateDirectory(Path.Join(_recordingsRoot!, TenantId)).FullName;
        await File.WriteAllBytesAsync(Path.Join(tenantDir, recorded.SessionId + ".wav"), RecordingBytes);

        using var host = new EventStoreReplicaFactory(
            ConnectionString("analytics"), ConnectionString("cluster"), sessionManager,
            new FakeVoiceLeader("replica-recorded", isLeader: false), postgresEventStore: true)
        {
            ApiKeyTenantId = TenantId,
            RecordingsBasePath = _recordingsRoot,
        };
        await HoldVoiceLeaseAsync(host);
        var subscriber = host.Services.GetRequiredService<EventStoreSubscriber>();
        await subscriber.StartAsync(CancellationToken.None);
        try
        {
            RaiseCall(source, recorded.SessionId, offsetSeconds: 0);
            RaiseCall(source, unrecorded.SessionId, offsetSeconds: 1);

            using var client = host.CreateAuthenticatedClient();
            var recordedDetail = await WaitForCdrDetailAsync(client, recorded.SessionId);
            var unrecordedDetail = await WaitForCdrDetailAsync(client, unrecorded.SessionId);

            // The list.
            var list = await GetJsonAsync(client, "/api/v1/analytics/cdr");
            var rows = list["data"]!.AsArray().Select(r => r!.AsObject())
                .ToDictionary(r => r["sessionId"]!.GetValue<string>());
            rows.Should().ContainKeys(recorded.SessionId, unrecorded.SessionId);
            rows[recorded.SessionId]["hasRecording"]!.GetValue<bool>().Should().BeTrue(
                "the recorded call's file is on disk, so its record names it");
            rows[unrecorded.SessionId]["hasRecording"]!.GetValue<bool>().Should().BeFalse(
                "a call with no recording file is not marked recorded, whatever the naming pattern would call it");

            // The recorded call's detail.
            var recordedCdr = recordedDetail["cdr"]!.AsObject();
            recordedCdr["sessionId"]!.GetValue<string>().Should().Be(recorded.SessionId,
                "the record's sessionId is its CallStarted event's session id");
            recordedCdr["hasRecording"]!.GetValue<bool>().Should().BeTrue();
            recordedDetail["recordingName"]?.GetValue<string>().Should().Be(recorded.SessionId,
                "the recording is named by the record's session id, the file the stream reads");
            var streamUrl = recordedDetail["recordingStreamUrl"]?.GetValue<string>();
            streamUrl.Should().NotBeNull("a recorded call's detail links its stream");
            streamUrl.Should().EndWith($"/{recorded.SessionId}/stream")
                .And.NotContainAny("?", "#", "%");

            // The unrecorded call's detail: no recording member at all.
            unrecordedDetail["cdr"]!["hasRecording"]!.GetValue<bool>().Should().BeFalse();
            unrecordedDetail.ContainsKey("recordingName").Should().BeFalse("an unrecorded call names no recording");
            unrecordedDetail.ContainsKey("recordingStreamUrl").Should().BeFalse("and links no stream");

            // The stream, requested as the console's player does: Authorization and X-Tenant-Id, the id unencoded.
            using var stream = await client.GetAsync($"/api/v1/recordings/{recorded.SessionId}/stream");
            stream.StatusCode.Should().Be(HttpStatusCode.OK, "the recorded call's stream serves its file");
            (await stream.Content.ReadAsByteArrayAsync()).Should().Equal(RecordingBytes);
            using var noStream = await client.GetAsync($"/api/v1/recordings/{unrecorded.SessionId}/stream");
            noStream.StatusCode.Should().Be(HttpStatusCode.NotFound, "an unrecorded call has no stream");
        }
        finally
        {
            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        _recordingsRoot = Directory.CreateTempSubdirectory("verbara-b4c-recordings-").FullName;
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
        if (_recordingsRoot is not null && Directory.Exists(_recordingsRoot))
            Directory.Delete(_recordingsRoot, recursive: true);
    }

    /// <summary>
    /// Makes the replica the event store's writer: the lease row its fence reads (D13), owned by its InstanceId, then its
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

    private static void RaiseCall(Subject<SessionDomainEvent> source, string sessionId, int offsetSeconds)
    {
        var start = BaseTime.AddSeconds(offsetSeconds);
        source.OnNext(new CallStartedEvent(sessionId, ServerId, start, CallDirection.Inbound, "5551234"));
        source.OnNext(new CallConnectedEvent(
            sessionId, ServerId, start.AddSeconds(5), "agent-1", "sales", TimeSpan.FromSeconds(5)));
        source.OnNext(new CallEndedEvent(
            sessionId, ServerId, start.AddSeconds(30), HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));
    }

    private static async Task<JsonObject> WaitForCdrDetailAsync(HttpClient client, string sessionId)
    {
        var deadline = DateTimeOffset.UtcNow + Settle;
        while (true)
        {
            using var response = await client.GetAsync($"/api/v1/analytics/cdr/{sessionId}");
            if (response.StatusCode == HttpStatusCode.OK)
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            if (response.StatusCode != HttpStatusCode.NotFound || DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"The CDR detail of {sessionId} answered {(int)response.StatusCode} within {Settle}.");
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
        $"Database=postgres;Username=postgres;Password=postgres;Application Name=eventstore-recorded-{role}";
}
