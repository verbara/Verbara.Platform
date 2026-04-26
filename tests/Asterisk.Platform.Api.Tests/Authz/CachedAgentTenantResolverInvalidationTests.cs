using System.Reactive.Subjects;
using Asterisk.Platform.Api.Authz;
using Asterisk.Sdk.Pro.Push.SignalR.Events;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Asterisk.Platform.Api.Tests.Authz;

/// <summary>
/// Verifies that <see cref="CachedAgentTenantResolver"/> subscribes to
/// <see cref="AgentTenantMembershipChangedEvent"/> on the Pro.Push backplane
/// and evicts the per-agent cache entry on receipt — closes the ADR-0005
/// §"Concerns" 5-min TTL gap (R5.3 Wave 2.5 task A.4.b / B.3 Set B item).
/// </summary>
public sealed class CachedAgentTenantResolverInvalidationTests
{
    [Fact]
    public async Task GetTenantIdAsync_ShouldRepopulateFromStore_WhenMembershipChangedEventReceived()
    {
        using var bus = new FakePushEventBus();
        var resolver = new TestResolver(bus) { TenantToReturn = "tenant-A" };

        // Populate the cache.
        var first = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);
        first.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(1);

        // Confirm cache is hot.
        var cached = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);
        cached.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(1, because: "second call must hit the cache.");

        // Lateral invalidation event fires.
        bus.Emit(new AgentTenantMembershipChangedEvent
        {
            AgentId = "agent-1",
            TenantId = "tenant-B",
            Action = AgentTenantMembershipAction.Updated,
            Metadata = MakeMetadata("tenant-B"),
        });

        // Next call must re-hit the store (cache evicted).
        resolver.TenantToReturn = "tenant-B";
        var afterEvict = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);

        afterEvict.Should().Be("tenant-B");
        resolver.LookupCount.Should().Be(2,
            because: "the membership-changed event must evict the cache entry, forcing a fresh lookup.");
    }

    [Fact]
    public async Task GetTenantIdAsync_ShouldNotEvictOtherAgents_WhenMembershipChangedEventReceived()
    {
        using var bus = new FakePushEventBus();
        var resolver = new TestResolver(bus)
        {
            Mapping = new()
            {
                ["agent-A"] = "tenant-A",
                ["agent-B"] = "tenant-B",
            },
        };

        // Populate both cache slots.
        await resolver.GetTenantIdAsync("agent-A", CancellationToken.None);
        await resolver.GetTenantIdAsync("agent-B", CancellationToken.None);
        resolver.LookupCount.Should().Be(2);

        // Event for agent-A only.
        bus.Emit(new AgentTenantMembershipChangedEvent
        {
            AgentId = "agent-A",
            TenantId = "tenant-A2",
            Action = AgentTenantMembershipAction.Updated,
            Metadata = MakeMetadata("tenant-A2"),
        });

        // agent-B cache entry must remain intact (no extra lookup).
        var b = await resolver.GetTenantIdAsync("agent-B", CancellationToken.None);
        b.Should().Be("tenant-B");
        resolver.LookupCount.Should().Be(2,
            because: "membership change for agent-A must not evict agent-B's cache entry.");

        // agent-A entry must have been evicted (one extra lookup).
        var a = await resolver.GetTenantIdAsync("agent-A", CancellationToken.None);
        a.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(3);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static PushEventMetadata MakeMetadata(string tenantId) =>
        new(TenantId: tenantId, UserId: null, OccurredAt: DateTimeOffset.UtcNow, CorrelationId: null);

    private sealed class FakePushEventBus : IPushEventBus, IDisposable
    {
        private readonly Subject<PushEvent> _subject = new();

        public void Dispose() => _subject.Dispose();

        public void Emit(PushEvent evt) => _subject.OnNext(evt);

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
            where TEvent : PushEvent
        {
            _subject.OnNext(pushEvent);
            return ValueTask.CompletedTask;
        }

        public IObservable<PushEvent> AsObservable() => _subject;

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent =>
            (IObservable<TEvent>)System.Reactive.Linq.Observable.OfType<TEvent>(_subject);
    }

    /// <summary>
    /// Test-only subclass overriding the protected DB lookup so we don't need Postgres.
    /// </summary>
    private sealed class TestResolver : CachedAgentTenantResolver
    {
        public string? TenantToReturn { get; set; }
        public Dictionary<string, string?>? Mapping { get; init; }
        public int LookupCount { get; private set; }

        public TestResolver(IPushEventBus eventBus)
            : base(
                NpgsqlDataSource.Create("Host=localhost;Database=ignored;Username=postgres"),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                eventBus,
                NullLogger<CachedAgentTenantResolver>.Instance)
        {
        }

        protected override Task<string?> LookupTenantIdAsync(string agentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LookupCount++;
            if (Mapping is not null)
                return Task.FromResult(Mapping.TryGetValue(agentId, out var mapped) ? mapped : null);
            return Task.FromResult(TenantToReturn);
        }
    }
}
