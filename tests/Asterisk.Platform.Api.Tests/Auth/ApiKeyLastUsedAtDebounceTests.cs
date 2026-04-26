using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Auth;

/// <summary>
/// Unit coverage for <see cref="ApiKeyLastUsedStamper"/> — the debounced
/// writer that backs R5.2 PC.5 / B.12. The stamper guards
/// <see cref="IApiKeyStore.UpdateLastUsedAsync"/> with an in-process
/// <see cref="IMemoryCache"/> so chatty clients cannot turn every request
/// into an UPDATE on <c>api_keys</c>.
/// </summary>
public sealed class ApiKeyLastUsedAtDebounceTests
{
    private static (ApiKeyLastUsedStamper Stamper, IApiKeyStore Store, TestClock Time, IMemoryCache Cache)
        CreateSut()
    {
        var store = Substitute.For<IApiKeyStore>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var time = new TestClock(new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));
        var logger = NullLogger<ApiKeyLastUsedStamper>.Instance;
        var stamper = new ApiKeyLastUsedStamper(store, cache, time, logger);
        return (stamper, store, time, cache);
    }

    [Fact]
    public async Task StampLastUsed_ShouldUpdate_WhenFirstUseAfterDebounceWindow()
    {
        var (stamper, store, time, _) = CreateSut();
        var keyId = EntityId.New();

        await stamper.StampAsync(keyId, CancellationToken.None);

        await store.Received(1).UpdateLastUsedAsync(
            keyId,
            time.GetUtcNow(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StampLastUsed_ShouldNotUpdate_WhenWithinDebounceWindow()
    {
        var (stamper, store, time, _) = CreateSut();
        var keyId = EntityId.New();

        // First call primes the cache.
        await stamper.StampAsync(keyId, CancellationToken.None);
        await store.Received(1).UpdateLastUsedAsync(
            keyId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        store.ClearReceivedCalls();

        // Advance well within the debounce window — second call is a no-op.
        time.Advance(TimeSpan.FromSeconds(30));
        await stamper.StampAsync(keyId, CancellationToken.None);

        await store.DidNotReceive().UpdateLastUsedAsync(
            Arg.Any<EntityId>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StampLastUsed_ShouldUpdate_WhenSecondUseExceedsDebounceWindow()
    {
        var (stamper, store, time, _) = CreateSut();
        var keyId = EntityId.New();

        await stamper.StampAsync(keyId, CancellationToken.None);
        store.ClearReceivedCalls();

        // Advance past the debounce window.
        time.Advance(ApiKeyLastUsedStamper.StampDebounce + TimeSpan.FromSeconds(1));
        var expectedTimestamp = time.GetUtcNow();
        await stamper.StampAsync(keyId, CancellationToken.None);

        await store.Received(1).UpdateLastUsedAsync(
            keyId,
            expectedTimestamp,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StampLastUsed_ShouldDebouncePerKey_WhenDifferentKeysUsedConcurrently()
    {
        // Sanity check: the debounce is keyed per key_id, not global.
        var (stamper, store, _, _) = CreateSut();
        var keyA = EntityId.New();
        var keyB = EntityId.New();

        await stamper.StampAsync(keyA, CancellationToken.None);
        await stamper.StampAsync(keyB, CancellationToken.None);

        await store.Received(1).UpdateLastUsedAsync(
            keyA, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await store.Received(1).UpdateLastUsedAsync(
            keyB, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StampLastUsed_ShouldSwallowStoreException_WhenUpdateFails()
    {
        // Auth must never break because of a stamp write failure.
        var (stamper, store, _, _) = CreateSut();
        var keyId = EntityId.New();

        store
            .UpdateLastUsedAsync(Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("connection lost"));

        var act = async () => await stamper.StampAsync(keyId, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Lightweight ad-hoc <see cref="TimeProvider"/> for the debouncer — same
    /// pattern as <c>SessionTimeoutServiceTests.TestClock</c>. Avoids pulling
    /// in <c>Microsoft.Extensions.TimeProvider.Testing</c> for two consumers.
    /// </summary>
    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
