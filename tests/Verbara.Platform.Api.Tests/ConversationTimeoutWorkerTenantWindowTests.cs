using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReturnsExtensions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.6 (design D11; spec <c>conversation-inactivity-close</c>, "A tenant's
/// inactivity window override MUST apply, and null MUST mean the host default"): the inactivity pass reads the
/// tenant's stored window once per tenant per pass through <see cref="ITenantAuthConfigStore"/>, and a SQL change of
/// it reaches the pass within the cache TTL plus one sweep interval plus one tick.
/// </summary>
public sealed partial class ConversationTimeoutWorkerTests
{
    private const string OtherTenantId = "t002";
    private const string ThirdTenantId = "t003";

    private static readonly EventId InvalidWindowEvent = new(7601);
    private static readonly EventId WindowBelowWhatsAppEvent = new(7602);

    [Fact]
    public async Task InactivityPass_ShouldApplyTheTenantWindow_WhenOneTenantStoresAnOverrideAndTheOtherNull()
    {
        using var h = new InactivityHarness();
        h.Tenants.Add(OtherTenantId);
        h.Tenants.Add(ThirdTenantId);
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = TestTenantId }, CancellationToken.None);
        await store.SaveAsync(new TenantAuthConfig { TenantId = OtherTenantId }, CancellationToken.None);
        store.SeedInactivityCloseAfterHours(TestTenantId, 24);
        store.SeedInactivityCloseAfterHours(OtherTenantId, null);
        h.TenantAuthConfig = store; // the third tenant has no row at all
        var a = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(25));
        var b = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(25), tenant: new TenantId(OtherTenantId));
        var c = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(25), tenant: new TenantId(ThirdTenantId));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(a)).State);
        Assert.Equal(ConversationState.Active, (await h.GetAsync(b)).State);
        Assert.Equal(ConversationState.Active, (await h.GetAsync(c)).State);
    }

    [Fact]
    public async Task InactivityPass_ShouldUseTheHostWindowAndWarnOnce_WhenTheStoredOverrideIsZero()
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        h.TenantAuthConfig = StoreWithOverride(TestTenantId, 0);
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(25));
        var worker = h.CreateWorker();

        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        h.Now = h.Now.AddSeconds(h.Options.InactivityCloseSweepIntervalSeconds);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(2, h.ListInactiveCalls); // both passes ran
        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        var warning = Assert.Single(logger.Entries, e => e.EventId.Id == InvalidWindowEvent.Id);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(TestTenantId, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InactivityPass_ShouldApplyTheOverrideAndWarnOnceAboutWhatsApp_WhenTheStoredOverrideIsTwelveHours()
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        h.TenantAuthConfig = StoreWithOverride(TestTenantId, 12);
        var idle13 = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(13));
        var idle11 = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(11));
        var worker = h.CreateWorker();

        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        h.Now = h.Now.AddSeconds(h.Options.InactivityCloseSweepIntervalSeconds);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(idle13)).State);
        Assert.Equal(ConversationState.Active, (await h.GetAsync(idle11)).State);
        var warning = Assert.Single(logger.Entries, e => e.EventId.Id == WindowBelowWhatsAppEvent.Id);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(TestTenantId, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == InvalidWindowEvent.Id);
    }

    [Fact]
    public async Task InactivityPass_ShouldReadTheTenantConfigOncePerTenantPerPass_WhenTheTenantHasManyIdleChats()
    {
        using var h = new InactivityHarness();
        h.Tenants.Add(OtherTenantId);
        var inner = StoreWithOverride(TestTenantId, 24);
        var counting = Substitute.For<ITenantAuthConfigStore>();
        counting.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => inner.GetAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        h.TenantAuthConfig = counting;
        for (var i = 0; i < 3; i++)
        {
            await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
            await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80), tenant: new TenantId(OtherTenantId));
        }
        var worker = h.CreateWorker();

        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        h.Now = h.Now.AddSeconds(5); // a tick with no pass due reads nothing
        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        h.Now = h.Now.AddSeconds(h.Options.InactivityCloseSweepIntervalSeconds);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        await counting.Received(2).GetAsync(TestTenantId, Arg.Any<CancellationToken>());
        await counting.Received(2).GetAsync(OtherTenantId, Arg.Any<CancellationToken>());
        Assert.Equal(4, counting.ReceivedCalls().Count());
    }

    [Fact]
    public async Task InactivityPass_ShouldSkipOnlyThatTenantAndWarn_WhenItsConfigCannotBeRead()
    {
        using var h = new InactivityHarness();
        h.Tenants.Add(OtherTenantId);
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        var failing = Substitute.For<ITenantAuthConfigStore>();
        failing.GetAsync(TestTenantId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        failing.GetAsync(OtherTenantId, Arg.Any<CancellationToken>()).ReturnsNull();
        h.TenantAuthConfig = failing;
        var unread = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(200));
        var other = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(200), tenant: new TenantId(OtherTenantId));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        // Closing by the host window could close chats a longer tenant window keeps open: the tenant waits a pass.
        Assert.Equal(ConversationState.Active, (await h.GetAsync(unread)).State);
        Assert.Equal(ConversationState.Closed, (await h.GetAsync(other)).State);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Exception is InvalidOperationException
            && e.Message.Contains(TestTenantId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InactivityPass_ShouldCloseNothingAndNotThrow_WhenTheStoredOverrideIsLargerThanTheCalendar()
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        h.TenantAuthConfig = StoreWithOverride(TestTenantId, int.MaxValue);
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(200));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    // ─── Override propagation bound (D11, audit round 3, F3) ──────────────────────────────────────────────────

    [Fact]
    public async Task InactivityPass_ShouldApplyASqlSetOverrideAtTheFirstPassAfterTheTtl_WhenTheIntervalIs300Seconds()
    {
        using var h = new InactivityHarness();
        h.Options.InactivityCloseSweepIntervalSeconds = 300;
        using var cached = new CachedHarnessStore(h);
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(30));
        var worker = h.CreateWorker();
        var t0 = h.Now;

        await TickAsync(h, worker, t0); // the pass warms the cache with the null override
        Assert.Null((await cached.Inner.GetAsync(TestTenantId, CancellationToken.None))!.InactivityCloseAfterHours);
        var updatedAt = t0.AddSeconds(1);
        cached.Inner.SeedInactivityCloseAfterHours(TestTenantId, 24); // the SQL UPDATE: invalidates nothing

        for (var s = 5; s <= 120; s += 5)
            await TickAsync(h, worker, t0.AddSeconds(s));
        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State); // TTL expired, no pass due yet

        var closedAt = await TickUntilClosedAsync(h, worker, conv, t0.AddSeconds(125), t0.AddSeconds(400));

        Assert.Equal(t0.AddSeconds(300), closedAt);
        Assert.True(closedAt - updatedAt <= TimeSpan.FromSeconds(60 + 300 + 5), "bound = TTL + interval + tick");
    }

    [Fact]
    public async Task InactivityPass_ShouldApplyASqlSetOverrideAtTheFirstPassAfterTheEntryExpired_WhenTheIntervalIs30Seconds()
    {
        using var h = new InactivityHarness();
        h.Options.InactivityCloseSweepIntervalSeconds = 30;
        using var cached = new CachedHarnessStore(h);
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(30));
        var worker = h.CreateWorker();
        var t0 = h.Now;

        await TickAsync(h, worker, t0); // the entry is set at 0 and expires at +60
        cached.Inner.SeedInactivityCloseAfterHours(TestTenantId, 24);

        for (var s = 5; s <= 30; s += 5)
            await TickAsync(h, worker, t0.AddSeconds(s));
        Assert.Equal(2, h.ListInactiveCalls); // the +30 pass ran, on the warm null entry
        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);

        var closedAt = await TickUntilClosedAsync(h, worker, conv, t0.AddSeconds(35), t0.AddSeconds(120));

        Assert.Equal(t0.AddSeconds(60), closedAt);
    }

    [Theory]
    [InlineData(30, null)] // hits the warm entry: changes nothing
    [InlineData(70, 24)]   // after expiry and after the update: re-warms the entry with the new value
    public async Task InactivityPass_ShouldStillCloseAtTheBound_WhenAnotherReaderTouchesTheCachedEntry(int readAtSeconds, int? readValue)
    {
        using var h = new InactivityHarness();
        h.Options.InactivityCloseSweepIntervalSeconds = 300;
        using var cached = new CachedHarnessStore(h);
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(30));
        var worker = h.CreateWorker();
        var t0 = h.Now;

        await TickAsync(h, worker, t0);
        cached.Inner.SeedInactivityCloseAfterHours(TestTenantId, 24);

        for (var s = 5; s < readAtSeconds; s += 5)
            await TickAsync(h, worker, t0.AddSeconds(s));
        h.Now = t0.AddSeconds(readAtSeconds);
        var login = await cached.Store.GetAsync(TestTenantId, CancellationToken.None); // a login-like read
        Assert.Equal(readValue, login!.InactivityCloseAfterHours);

        var closedAt = await TickUntilClosedAsync(h, worker, conv, t0.AddSeconds(readAtSeconds + 5), t0.AddSeconds(400));

        Assert.Equal(t0.AddSeconds(300), closedAt);
        Assert.True(closedAt - t0.AddSeconds(1) <= TimeSpan.FromSeconds(365));
    }

    private static InMemoryTenantAuthConfigStore StoreWithOverride(string tenantId, int? hours)
    {
        var store = new InMemoryTenantAuthConfigStore();
        store.SaveAsync(new TenantAuthConfig { TenantId = tenantId }, CancellationToken.None).GetAwaiter().GetResult();
        store.SeedInactivityCloseAfterHours(tenantId, hours);
        return store;
    }

    private static async Task TickAsync(InactivityHarness h, ConversationTimeoutWorker worker, DateTimeOffset at)
    {
        h.Now = at;
        await worker.ProcessTimeoutsAsync(CancellationToken.None);
    }

    /// <summary>Ticks every 5 s from <paramref name="from"/> and returns the tick at which the chat was closed.</summary>
    private static async Task<DateTimeOffset?> TickUntilClosedAsync(
        InactivityHarness h, ConversationTimeoutWorker worker, Conversation conv, DateTimeOffset from, DateTimeOffset until)
    {
        for (var at = from; at <= until; at = at.AddSeconds(5))
        {
            await TickAsync(h, worker, at);
            if ((await h.GetAsync(conv)).State == ConversationState.Closed)
                return at;
        }
        return null;
    }

    /// <summary>
    /// The real <see cref="CachedTenantAuthConfigStore"/> (60 s TTL) over the in-memory store, its
    /// <see cref="MemoryCache"/> driven by the harness clock, so cache expiry and the worker's passes share one time.
    /// The tenant's row exists with a null override. The inner store is read through <see cref="MaterializingStore"/>:
    /// the in-memory store hands out (and a seed mutates) its live entity, which would change the cached value under the
    /// cache; the Postgres store materializes a fresh object per read.
    /// </summary>
    private sealed class CachedHarnessStore : IDisposable
    {
        private readonly MemoryCache _cache;

        public CachedHarnessStore(InactivityHarness h)
        {
            _cache = new MemoryCache(new MemoryCacheOptions { Clock = new HarnessSystemClock(h) });
            Inner.SaveAsync(new TenantAuthConfig { TenantId = TestTenantId }, CancellationToken.None).GetAwaiter().GetResult();
            Store = new CachedTenantAuthConfigStore(new MaterializingStore(Inner), _cache);
            h.TenantAuthConfig = Store;
        }

        public InMemoryTenantAuthConfigStore Inner { get; } = new();

        public CachedTenantAuthConfigStore Store { get; }

        public void Dispose() => _cache.Dispose();
    }

    private sealed class MaterializingStore(ITenantAuthConfigStore inner) : ITenantAuthConfigStore
    {
        public async Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct) =>
            await inner.GetAsync(tenantId, ct) is { } row
                ? new TenantAuthConfig { TenantId = row.TenantId, InactivityCloseAfterHours = row.InactivityCloseAfterHours }
                : null;

        public Task SaveAsync(TenantAuthConfig config, CancellationToken ct) => inner.SaveAsync(config, ct);
    }

    private sealed class HarnessSystemClock(InactivityHarness h) : ISystemClock
    {
        public DateTimeOffset UtcNow => h.Now;
    }
}
