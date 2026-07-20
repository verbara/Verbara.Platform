using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Events;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0012 gate #6 best-effort contract: the admin write-paths sync to Asterisk
/// Realtime on a BEST-EFFORT basis — a sync failure must be swallowed (now logged,
/// no longer an empty catch) because the Pro realtime reconciler re-converges on
/// its next pass, so the admin write itself must still succeed. This class runs on
/// a factory whose IRealtimeSyncService throws on every call, then drives each of
/// the seven best-effort catch sites and asserts the write still returns success —
/// covering both the contract and the catch bodies the empty-catch remediation
/// introduced.
/// </summary>
public sealed class AdminEndpointRealtimeFailureTests
    : IClassFixture<AdminEndpointRealtimeFailureTests.ThrowingSyncApiFactory>
{
    private readonly HttpClient _client;

    public AdminEndpointRealtimeFailureTests(ThrowingSyncApiFactory factory)
        => _client = factory.CreateAuthenticatedClient();

    [Fact]
    public async Task CreateQueue_ShouldReturn201_WhenRealtimeSyncThrows()
    {
        var response = await _client.PostAsync("/api/admin/queues",
            JsonContent.Create(new { name = "q-sync-fail-create" }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateQueue_ShouldReturn200_WhenRealtimeSyncThrows()
    {
        var create = await _client.PostAsync("/api/admin/queues",
            JsonContent.Create(new { name = "q-sync-fail-update" }));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _client.PutAsync($"/api/admin/queues/{id}",
            JsonContent.Create(new { maxWaiting = 5 }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteQueue_ShouldReturn204_WhenRealtimeSyncThrows()
    {
        var create = await _client.PostAsync("/api/admin/queues",
            JsonContent.Create(new { name = "q-sync-fail-delete" }));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/queues/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreateAgent_ShouldReturn201_WhenAgentSyncThrows()
    {
        var response = await _client.PostAsync("/api/admin/agents",
            JsonContent.Create(new
            {
                userId = "user-sync-fail-a",
                displayName = "Sync Fail Agent",
                extension = "7001",
                sipPassword = "s",
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateAgent_ShouldReturn201_WhenAddQueueMemberThrows()
    {
        var queue = await _client.PostAsync("/api/admin/queues",
            JsonContent.Create(new { name = "q-member-fail" }));
        var queueId = JsonNode.Parse(await queue.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _client.PostAsync("/api/admin/agents",
            JsonContent.Create(new
            {
                userId = "user-sync-fail-m",
                displayName = "Member Fail Agent",
                queueMemberships = new[] { new { queueId, penalty = 0 } },
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateAgent_ShouldReturn200_WhenAgentSyncThrows()
    {
        var create = await _client.PostAsync("/api/admin/agents",
            JsonContent.Create(new { userId = "user-sync-fail-u", displayName = "Update Sync Agent" }));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["agentId"]!.GetValue<string>();

        var response = await _client.PutAsync($"/api/admin/agents/{id}",
            JsonContent.Create(new { extension = "7002", sipPassword = "s" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteAgent_ShouldReturn204_WhenRealtimeSyncThrows()
    {
        var create = await _client.PostAsync("/api/admin/agents",
            JsonContent.Create(new
            {
                userId = "user-sync-fail-d",
                displayName = "Delete Sync Agent",
                extension = "7003",
                sipPassword = "s",
            }));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["agentId"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/agents/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>Factory whose realtime sync throws on every operation.</summary>
    public sealed class ThrowingSyncApiFactory : AuthenticatedPlatformApiFactory
    {
        protected override IRealtimeSyncService CreateRealtimeSyncService()
            => new ThrowingRealtimeSyncService();
    }

    /// <summary>Every sync operation throws — models a fully unavailable realtime backend.</summary>
    private sealed class ThrowingRealtimeSyncService : IRealtimeSyncService
    {
        private static ValueTask Boom() =>
            throw new InvalidOperationException("realtime sync unavailable (test)");

        private static ValueTask<T> Boom<T>() =>
            throw new InvalidOperationException("realtime sync unavailable (test)");

        public IObservable<RealtimeSyncEvent> Events => NeverObservable.Instance;

        public ValueTask SyncAgentAsync(string tenantId, string agentId, string displayName,
            string extension, string sipPassword, long? profileId = null, CancellationToken ct = default) => Boom();
        public ValueTask RemoveAgentAsync(string tenantId, string agentId, CancellationToken ct = default) => Boom();
        public ValueTask SyncQueueAsync(string tenantId, string queueName,
            RealtimeQueueOptions options, CancellationToken ct = default) => Boom();
        public ValueTask RemoveQueueAsync(string tenantId, string queueName, CancellationToken ct = default) => Boom();
        public ValueTask AddQueueMemberAsync(string tenantId, string queueName, string agentId,
            string displayName, int penalty = 0, IReadOnlyList<string>? allowedChannels = null,
            CancellationToken ct = default) => Boom();
        public ValueTask RemoveQueueMemberAsync(string tenantId, string queueName,
            string agentId, CancellationToken ct = default) => Boom();
        public ValueTask<bool> QueueMemberExistsAsync(string tenantId, string queueName,
            string agentId, CancellationToken ct = default) => Boom<bool>();
        public ValueTask SyncAgentPausedAsync(string tenantId, string agentId, bool paused,
            CancellationToken ct = default) => Boom();
        public ValueTask SyncTrunkAsync(string tenantId, Trunk trunk, CancellationToken ct = default) => Boom();
        public ValueTask RemoveTrunkAsync(string tenantId, long trunkId, CancellationToken ct = default) => Boom();
        public ValueTask SyncAgentBatchAsync(string tenantId, IReadOnlyList<AgentSyncRequest> agents,
            CancellationToken ct = default) => Boom();
        public ValueTask SyncQueueBatchAsync(string tenantId, IReadOnlyList<QueueSyncRequest> queues,
            CancellationToken ct = default) => Boom();
        public ValueTask ProvisionTenantAsync(string tenantId, CancellationToken ct = default) => Boom();
        public ValueTask CleanupTenantAsync(string tenantId, CancellationToken ct = default) => Boom();

        private sealed class NeverObservable : IObservable<RealtimeSyncEvent>
        {
            public static readonly NeverObservable Instance = new();
            public IDisposable Subscribe(IObserver<RealtimeSyncEvent> observer) => NoopDisposable.Instance;

            private sealed class NoopDisposable : IDisposable
            {
                public static readonly NoopDisposable Instance = new();
                public void Dispose() { }
            }
        }
    }
}
