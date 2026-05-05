using System.Reactive.Subjects;
using Verbara.Platform.Api.Authz;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Verbara.Platform.Api.Tests.Authz;

/// <summary>
/// Verifies the cache + error-handling behavior of <see cref="CachedAgentTenantResolver"/>.
/// The DB-lookup half of the resolver is exercised via a test subclass that overrides the
/// protected virtual <c>LookupTenantIdAsync</c> method so we can run without Postgres.
/// (Platform repo has no Testcontainers fixture; the DB SQL itself is trivial — column read.)
/// </summary>
public sealed class CachedAgentTenantResolverTests
{
    [Fact]
    public async Task GetTenantIdAsync_ShouldReturnTenantId_OnFirstCall()
    {
        var resolver = new TestResolver { TenantToReturn = "tenant-A" };

        var result = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);

        result.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTenantIdAsync_ShouldServeFromCache_OnSubsequentCalls()
    {
        var resolver = new TestResolver { TenantToReturn = "tenant-A" };

        await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);
        var second = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);
        var third = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);

        second.Should().Be("tenant-A");
        third.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(1, because: "subsequent lookups for the same agentId hit the cache.");
    }

    [Fact]
    public async Task GetTenantIdAsync_ShouldCacheNegativeResult_WhenAgentUnknown()
    {
        var resolver = new TestResolver { TenantToReturn = null };

        var first = await resolver.GetTenantIdAsync("missing-agent", CancellationToken.None);
        var second = await resolver.GetTenantIdAsync("missing-agent", CancellationToken.None);

        first.Should().BeNull();
        second.Should().BeNull();
        resolver.LookupCount.Should().Be(1,
            because: "negative lookups are cached so a pathological caller cannot pin a DB connection per failed enumeration attempt.");
    }

    [Fact]
    public async Task GetTenantIdAsync_ShouldNotCacheTransientFailures()
    {
        var resolver = new TestResolver
        {
            TenantToReturn = "tenant-A",
            ThrowOnFirstLookup = true,
        };

        // First call fails -> resolver returns null, but does NOT cache.
        var first = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);
        // Second call hits the DB again and succeeds.
        var second = await resolver.GetTenantIdAsync("agent-1", CancellationToken.None);

        first.Should().BeNull();
        second.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(2);
    }

    [Fact]
    public async Task GetTenantIdAsync_ShouldThrowOnCancellation()
    {
        var resolver = new TestResolver { TenantToReturn = "tenant-A" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await resolver.GetTenantIdAsync("agent-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetTenantIdAsync_ShouldKeepDifferentAgents_InSeparateCacheSlots()
    {
        var resolver = new TestResolver { Mapping = new()
        {
            ["agent-A"] = "tenant-A",
            ["agent-B"] = "tenant-B",
        } };

        var a = await resolver.GetTenantIdAsync("agent-A", CancellationToken.None);
        var b = await resolver.GetTenantIdAsync("agent-B", CancellationToken.None);
        var aAgain = await resolver.GetTenantIdAsync("agent-A", CancellationToken.None);

        a.Should().Be("tenant-A");
        b.Should().Be("tenant-B");
        aAgain.Should().Be("tenant-A");
        resolver.LookupCount.Should().Be(2,
            because: "each unique agentId triggers exactly one lookup; subsequent calls for the same id are cache hits.");
    }

    /// <summary>
    /// Subclass of <see cref="CachedAgentTenantResolver"/> that overrides
    /// <c>LookupTenantIdAsync</c> so tests don't need a live Postgres.
    /// </summary>
    private sealed class TestResolver : CachedAgentTenantResolver
    {
        public string? TenantToReturn { get; init; }
        public Dictionary<string, string?>? Mapping { get; init; }
        public bool ThrowOnFirstLookup { get; set; }
        public int LookupCount { get; private set; }

        public TestResolver()
            : base(
                NpgsqlDataSource.Create("Host=localhost;Database=ignored;Username=postgres"),
                new MemoryCache(new MemoryCacheOptions()),
                new NoopPushEventBus(),
                NullLogger<CachedAgentTenantResolver>.Instance)
        {
        }

        protected override Task<string?> LookupTenantIdAsync(string agentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LookupCount++;
            if (ThrowOnFirstLookup && LookupCount == 1)
                throw new InvalidOperationException("simulated transient failure");

            if (Mapping is not null)
                return Task.FromResult(Mapping.TryGetValue(agentId, out var mapped) ? mapped : null);

            return Task.FromResult(TenantToReturn);
        }
    }

    /// <summary>
    /// No-op <see cref="IPushEventBus"/> for tests that don't exercise the
    /// lateral-invalidation path. Subscribing returns a disposable that does
    /// nothing; publishes are silently dropped.
    /// </summary>
    private sealed class NoopPushEventBus : IPushEventBus, IDisposable
    {
        private readonly Subject<PushEvent> _subject = new();

        public void Dispose() => _subject.Dispose();

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
            where TEvent : PushEvent => ValueTask.CompletedTask;

        public IObservable<PushEvent> AsObservable() => _subject;

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent =>
            (IObservable<TEvent>)System.Reactive.Linq.Observable.OfType<TEvent>(_subject);
    }
}
