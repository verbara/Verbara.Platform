using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryAgentLivenessStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private const string NodeId = "node-a";

    [Fact]
    public async Task IsAliveAsync_ShouldReturnTrue_WhenTouchedWithinTtl()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryAgentLivenessStore(clock);
        var agentId = EntityId.New();

        await store.TouchAsync(Tenant, agentId, TimeSpan.FromSeconds(60), NodeId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));

        var alive = await store.IsAliveAsync(Tenant, agentId, CancellationToken.None);

        alive.Should().BeTrue();
    }

    [Fact]
    public async Task IsAliveAsync_ShouldReturnFalse_WhenTtlElapsed()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryAgentLivenessStore(clock);
        var agentId = EntityId.New();

        await store.TouchAsync(Tenant, agentId, TimeSpan.FromSeconds(60), NodeId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));

        var alive = await store.IsAliveAsync(Tenant, agentId, CancellationToken.None);

        alive.Should().BeFalse();
    }

    [Fact]
    public async Task IsAliveAsync_ShouldReturnFalse_WhenNeverTouched()
    {
        var store = new InMemoryAgentLivenessStore(new TestClock(DateTimeOffset.UnixEpoch));

        var alive = await store.IsAliveAsync(Tenant, EntityId.New(), CancellationToken.None);

        alive.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_ShouldMakeAgentNotAlive_WhenCalledAfterTouch()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryAgentLivenessStore(clock);
        var agentId = EntityId.New();

        await store.TouchAsync(Tenant, agentId, TimeSpan.FromSeconds(60), NodeId, CancellationToken.None);
        await store.RemoveAsync(Tenant, agentId, CancellationToken.None);

        var alive = await store.IsAliveAsync(Tenant, agentId, CancellationToken.None);

        alive.Should().BeFalse();
    }

    [Fact]
    public async Task TouchAsync_ShouldRefreshExpiry_WhenCalledAgainBeforeTtl()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryAgentLivenessStore(clock);
        var agentId = EntityId.New();

        await store.TouchAsync(Tenant, agentId, TimeSpan.FromSeconds(60), NodeId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(40));
        // Re-touch before the original window elapses — expiry resets to now + 60s.
        await store.TouchAsync(Tenant, agentId, TimeSpan.FromSeconds(60), NodeId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(40)); // 80s past the FIRST touch, 40s past the SECOND

        var alive = await store.IsAliveAsync(Tenant, agentId, CancellationToken.None);

        alive.Should().BeTrue();
    }

    /// <summary>Tiny ad-hoc <see cref="TimeProvider"/> for deterministic TTL tests.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
