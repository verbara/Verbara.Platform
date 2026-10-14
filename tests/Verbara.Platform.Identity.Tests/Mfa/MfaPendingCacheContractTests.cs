using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Identity.Tests.Mfa;

/// <summary>
/// The one behavioural contract of <see cref="IMfaPendingCache"/>, run against every implementation:
/// the in-memory cache here, and the Redis cache in <c>Verbara.Platform.Identity.Redis.Tests</c>, which
/// compiles this file as a link. Identical behaviour is proven by running the same assertions, not by
/// keeping two copies in step.
/// </summary>
public abstract class MfaPendingCacheContractTests
{
    /// <summary>A fresh cache with an isolated key space.</summary>
    protected abstract IMfaPendingCache CreateCache();

    /// <summary>
    /// The caches the concurrency cases race on, all over one key space. The Redis subclass returns caches
    /// on separate connections to stand in for separate replicas.
    /// </summary>
    protected virtual IReadOnlyList<IMfaPendingCache> CreateRacingCaches()
    {
        var cache = CreateCache();
        return [cache, cache];
    }

    protected static MfaPendingEntry LiveEntry(string userId = "user-1") => new()
    {
        UserId = userId,
        TenantId = "tenant-a",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };

    protected static string NewToken() => $"token-{Guid.NewGuid():N}";

    // ─── Sequential lifecycle ───────────────────────────────────────────────

    [Fact]
    public async Task TryBeginAttemptAsync_ShouldReserveThreeAttemptsThenReturnNull_WhenNeverConsumed()
    {
        var cache = CreateCache();
        var token = NewToken();
        await cache.StoreAsync(token, LiveEntry(), CancellationToken.None);

        var first = await cache.TryBeginAttemptAsync(token, CancellationToken.None);
        var second = await cache.TryBeginAttemptAsync(token, CancellationToken.None);
        var third = await cache.TryBeginAttemptAsync(token, CancellationToken.None);
        var fourth = await cache.TryBeginAttemptAsync(token, CancellationToken.None);

        first.Should().NotBeNull();
        first!.Attempt.Should().Be(1);
        first.IsFinal.Should().BeFalse();
        first.Entry.UserId.Should().Be("user-1");
        first.Entry.TenantId.Should().Be("tenant-a");
        second.Should().NotBeNull();
        second!.Attempt.Should().Be(2);
        second.IsFinal.Should().BeFalse();
        third.Should().NotBeNull();
        third!.Attempt.Should().Be(MfaChallenge.MaxAttempts);
        third.IsFinal.Should().BeTrue(because: "the attempt that reaches the limit is the last one");
        fourth.Should().BeNull(because: "the final reservation removed the challenge");
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldReturnFalse_WhenFinalAttemptAlreadyRemovedChallenge()
    {
        var cache = CreateCache();
        var token = NewToken();
        await cache.StoreAsync(token, LiveEntry(), CancellationToken.None);
        for (var i = 0; i < MfaChallenge.MaxAttempts; i++)
            await cache.TryBeginAttemptAsync(token, CancellationToken.None);

        var consumed = await cache.TryConsumeAsync(token, CancellationToken.None);

        consumed.Should().BeFalse(because: "a non-final racer must lose to the request holding the final attempt");
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldReturnTrueOnce_WhenChallengeIsLive()
    {
        var cache = CreateCache();
        var token = NewToken();
        await cache.StoreAsync(token, LiveEntry(), CancellationToken.None);
        await cache.TryBeginAttemptAsync(token, CancellationToken.None);

        var first = await cache.TryConsumeAsync(token, CancellationToken.None);
        var second = await cache.TryConsumeAsync(token, CancellationToken.None);
        var attemptAfter = await cache.TryBeginAttemptAsync(token, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
        attemptAfter.Should().BeNull(because: "a consumed challenge is unknown from then on");
    }

    [Fact]
    public async Task TryBeginAttemptAsync_ShouldReturnNull_WhenKeyMissing()
    {
        var cache = CreateCache();

        var attempt = await cache.TryBeginAttemptAsync("does-not-exist", CancellationToken.None);
        var consumed = await cache.TryConsumeAsync("does-not-exist", CancellationToken.None);

        attempt.Should().BeNull();
        consumed.Should().BeFalse();
    }

    [Fact]
    public async Task TryBeginAttemptAsync_ShouldReturnNull_WhenEntryExpired()
    {
        var cache = CreateCache();
        var token = NewToken();
        await cache.StoreAsync(token, new MfaPendingEntry
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
        }, CancellationToken.None);

        var attempt = await cache.TryBeginAttemptAsync(token, CancellationToken.None);
        var consumed = await cache.TryConsumeAsync(token, CancellationToken.None);

        attempt.Should().BeNull(because: "an expired challenge is never returned");
        consumed.Should().BeFalse(because: "an expired challenge cannot be consumed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TryBeginAttemptAsync_ShouldReturnNullWithoutThrowing_WhenTokenIsNullOrEmpty(string? token)
    {
        var cache = CreateCache();

        var attempt = await cache.TryBeginAttemptAsync(token, CancellationToken.None);
        var consumed = await cache.TryConsumeAsync(token, CancellationToken.None);

        attempt.Should().BeNull(because: "an empty token names no challenge, and every cache must agree "
                                       + "instead of throwing (a 500 at the endpoint)");
        consumed.Should().BeFalse();
    }

    [Fact]
    public async Task StoreAsync_ShouldResetAttemptCount_WhenTokenIsStoredAgain()
    {
        var cache = CreateCache();
        var token = NewToken();
        await cache.StoreAsync(token, LiveEntry("user-old"), CancellationToken.None);
        await cache.TryBeginAttemptAsync(token, CancellationToken.None);
        await cache.TryBeginAttemptAsync(token, CancellationToken.None);

        await cache.StoreAsync(token, LiveEntry("user-new"), CancellationToken.None);
        var attempt = await cache.TryBeginAttemptAsync(token, CancellationToken.None);

        attempt.Should().NotBeNull();
        attempt!.Attempt.Should().Be(1, because: "storing replaces the challenge, attempts included");
        attempt.IsFinal.Should().BeFalse();
        attempt.Entry.UserId.Should().Be("user-new");
    }

    [Fact]
    public async Task StoreAsync_ShouldKeepTokensIndependent_WhenTwoChallengesAreLive()
    {
        var cache = CreateCache();
        var a = NewToken();
        var b = NewToken();
        await cache.StoreAsync(a, LiveEntry("user-a"), CancellationToken.None);
        await cache.StoreAsync(b, LiveEntry("user-b"), CancellationToken.None);
        for (var i = 0; i < MfaChallenge.MaxAttempts; i++)
            await cache.TryBeginAttemptAsync(a, CancellationToken.None);

        var fromB = await cache.TryBeginAttemptAsync(b, CancellationToken.None);

        fromB.Should().NotBeNull();
        fromB!.Attempt.Should().Be(1, because: "spending one challenge spends no other");
        fromB.Entry.UserId.Should().Be("user-b");
    }

    // ─── Concurrency ────────────────────────────────────────────────────────

    [Fact]
    public async Task TryBeginAttemptAsync_ShouldGrantExactlyThreeReservations_WhenReservationsRace()
    {
        const int racers = 32;
        var caches = CreateRacingCaches();
        var token = NewToken();
        await caches[0].StoreAsync(token, LiveEntry(), CancellationToken.None);

        var results = await RaceAsync(racers, i => caches[i % caches.Count].TryBeginAttemptAsync(token, CancellationToken.None).AsTask());

        var granted = results.Where(r => r is not null).Select(r => r!).ToList();
        granted.Should().HaveCount(MfaChallenge.MaxAttempts,
            because: "racing reservations on one challenge can never exceed the attempt limit");
        granted.Count(r => r.IsFinal).Should().Be(1, because: "exactly one racer holds the final attempt");
        granted.Select(r => r.Attempt).Should().BeEquivalentTo([1, 2, 3]);
        (await caches[^1].TryBeginAttemptAsync(token, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldLetExactlyOneCallerWin_WhenConsumesRace()
    {
        const int racers = 8;
        var caches = CreateRacingCaches();
        var token = NewToken();
        await caches[0].StoreAsync(token, LiveEntry(), CancellationToken.None);

        var results = await RaceAsync(racers, i => caches[i % caches.Count].TryConsumeAsync(token, CancellationToken.None).AsTask());

        results.Count(r => r).Should().Be(1, because: "of racing correct factors at most one gets a session");
    }

    private static async Task<T[]> RaceAsync<T>(int racers, Func<int, Task<T>> action)
    {
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, racers)
            .Select(i => Task.Run(async () =>
            {
                start.Wait();
                return await action(i);
            }))
            .ToArray();
        start.Set();
        return await Task.WhenAll(tasks);
    }
}
