using System.Security.Cryptography;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Identity.Tests.Auth.Jwt;

public sealed class JwtValidationKeySnapshotTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly List<Func<Task<IReadOnlyList<JwtKeyEntry>>>> _answers = [];
    private int _loads;

    private JwtKeyEntry NewKey(string kid, TimeSpan? lifetime = null) => new()
    {
        KeyId = kid,
        Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Algorithm = JwtKeyAlgorithm.Hs256,
        ActivatedAt = _clock.GetUtcNow(),
        ExpiresAt = _clock.GetUtcNow() + (lifetime ?? TimeSpan.FromHours(1)),
        IsActive = true,
    };

    private JwtValidationKeySnapshot NewSnapshot(JwtValidationKeySnapshotOptions? options = null) => new(
        _ =>
        {
            var call = Interlocked.Increment(ref _loads) - 1;
            return _answers[Math.Min(call, _answers.Count - 1)]();
        },
        entry => new SymmetricSecurityKey(Convert.FromBase64String(entry.Key)) { KeyId = entry.KeyId },
        options,
        _clock);

    private void Answer(params JwtKeyEntry[] keys) =>
        _answers.Add(() => Task.FromResult<IReadOnlyList<JwtKeyEntry>>(keys));

    private void Fail() =>
        _answers.Add(() => Task.FromException<IReadOnlyList<JwtKeyEntry>>(new InvalidOperationException("pool down")));

    private static IEnumerable<string> Kids(JwtValidationKeySnapshot snapshot) =>
        snapshot.Resolve(null).Select(k => k.KeyId);

    [Fact]
    public async Task EnsureLoadedAsync_ShouldLoadOnce_WhenManyRequestsArriveCold()
    {
        var k1 = NewKey("k1");
        Answer(k1);
        var snapshot = NewSnapshot();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => snapshot.EnsureLoadedAsync(CancellationToken.None)));

        _loads.Should().Be(1);
        snapshot.IsLoaded.Should().BeTrue();
        Kids(snapshot).Should().Equal("k1");
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldReturnAfterTheFirstLoadWait_WhenThePoolNeverAnswers()
    {
        _answers.Add(() => new TaskCompletionSource<IReadOnlyList<JwtKeyEntry>>().Task);
        var snapshot = NewSnapshot(new JwtValidationKeySnapshotOptions { FirstLoadWait = TimeSpan.FromSeconds(5) });

        var waiting = snapshot.EnsureLoadedAsync(CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(5));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        snapshot.Resolve("k1").Should().BeEmpty();
        snapshot.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_ShouldKeepThePreviousKeys_WhenAReloadReturnsNoLiveKey()
    {
        var k1 = NewKey("k1");
        Answer(k1);
        Answer(NewKey("expired", TimeSpan.FromMinutes(-1)));
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(6));
        _ = snapshot.Resolve("k1");
        await snapshot.ReloadInFlight;

        _loads.Should().Be(2);
        Kids(snapshot).Should().Equal("k1");
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldReadAgainAfterTheInitialBackoffWithoutGrowing_WhenThePoolHasNoKey()
    {
        // A pool with no key yet (a fresh install before setup), or a read that silently skipped a
        // disconnected server, serves no key either way. It is a successful empty load, read again
        // after 1 s, not a failure whose wait grows to 30 s: kept for 5 minutes, it would refuse
        // tokens that long; counted as failed, it would refuse them up to 30 s.
        Answer();
        Answer();
        Answer(NewKey("k1"));
        var snapshot = NewSnapshot();

        await snapshot.EnsureLoadedAsync(CancellationToken.None);
        snapshot.IsLoaded.Should().BeTrue();
        Kids(snapshot).Should().BeEmpty();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);
        _loads.Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await snapshot.EnsureLoadedAsync(CancellationToken.None);
        await snapshot.ReloadInFlight;
        _loads.Should().Be(2);

        _clock.Advance(TimeSpan.FromSeconds(1));      // not 2 s: an empty read does not double the wait
        await snapshot.EnsureLoadedAsync(CancellationToken.None);
        await snapshot.ReloadInFlight;
        _loads.Should().Be(3);
        Kids(snapshot).Should().Equal("k1");
    }

    [Fact]
    public async Task Resolve_ShouldKeepUnexpiredKeysAndRetry_WhenAReloadLeavesThemOut()
    {
        var k1 = NewKey("k1");
        var k2 = NewKey("k2");
        Answer(k1, k2);
        Answer(k2);          // a partial read: k1 is still live in the pool
        Answer(k1, k2);
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(6));
        _ = snapshot.Resolve(null);
        await snapshot.ReloadInFlight;
        Kids(snapshot).Should().BeEquivalentTo(["k1", "k2"]);

        // Counted as failed: retried after the 1 s backoff, not after the 5 min refresh.
        _ = snapshot.Resolve(null);
        _loads.Should().Be(2);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _ = snapshot.Resolve(null);
        await snapshot.ReloadInFlight;
        _loads.Should().Be(3);
    }

    [Fact]
    public async Task Resolve_ShouldBackOff_WhenReloadsFail()
    {
        Answer(NewKey("k1"));
        Fail();
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(6));
        _ = snapshot.Resolve(null);
        await snapshot.ReloadInFlight;          // failure 1 → 1 s backoff
        _ = snapshot.Resolve(null);
        _loads.Should().Be(2);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _ = snapshot.Resolve(null);
        await snapshot.ReloadInFlight;          // failure 2 → 2 s backoff
        _loads.Should().Be(3);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _ = snapshot.Resolve(null);
        _loads.Should().Be(3);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _ = snapshot.Resolve(null);
        await snapshot.ReloadInFlight;
        _loads.Should().Be(4);

        Kids(snapshot).Should().Equal("k1");
    }

    [Fact]
    public async Task Resolve_ShouldRateLimitUnknownKeyIdReloads()
    {
        Answer(NewKey("k1"));
        var snapshot = NewSnapshot(new JwtValidationKeySnapshotOptions { UnknownKeyIdReloadInterval = TimeSpan.FromSeconds(10) });
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
            _ = snapshot.Resolve("unknown");
        await snapshot.ReloadInFlight;
        _loads.Should().Be(2);

        // Inside the interval: no reload now, one scheduled for when the interval ends.
        _clock.Advance(TimeSpan.FromSeconds(9));
        _ = snapshot.Resolve("unknown");
        _ = snapshot.Resolve("unknown");
        var scheduled = snapshot.PendingUnknownKeyIdReload;
        _loads.Should().Be(2);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await scheduled.WaitAsync(TimeSpan.FromSeconds(10));
        _loads.Should().Be(3);
    }

    [Fact]
    public async Task Add_ShouldKeepTheKey_WhenALaterReloadFails()
    {
        Answer(NewKey("k1"));
        Fail();
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        snapshot.Add(NewKey("k2"));
        _clock.Advance(TimeSpan.FromMinutes(6));
        _ = snapshot.Resolve(null);
        await snapshot.ReloadInFlight;

        Kids(snapshot).Should().BeEquivalentTo(["k1", "k2"]);
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldLoadInFull_WhenOnlyAnAddedKeyIsHeld()
    {
        Answer(NewKey("k1"), NewKey("k0"));
        var snapshot = NewSnapshot();
        snapshot.Add(NewKey("k1"));
        snapshot.IsLoaded.Should().BeFalse();

        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        snapshot.IsLoaded.Should().BeTrue();
        Kids(snapshot).Should().BeEquivalentTo(["k0", "k1"]);
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldNotWait_WhenAColdLoadFailedAndTheBackoffRuns()
    {
        Fail();
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);
        _loads.Should().Be(1);

        var second = snapshot.EnsureLoadedAsync(CancellationToken.None);
        second.IsCompletedSuccessfully.Should().BeTrue();
        _loads.Should().Be(1);
    }

    // ─── A key the snapshot has never seen (first key after setup, rotation, forged kids) ────────

    [Fact]
    public async Task EnsureLoadedAsync_ShouldAcceptTheFirstKey_WhenItAppearsAfterTheProbesFoundAnEmptyPool()
    {
        // A fresh install: health probes and the setup screen reach the host for minutes before the
        // first signing key exists. Then setup creates it and the admin's first request carries it.
        for (var i = 0; i < 8; i++)
            Answer();
        var k1 = NewKey("k1");
        Answer(k1);
        var snapshot = NewSnapshot();
        for (var i = 0; i < 8; i++)
        {
            if (i > 0)
                _clock.Advance(TimeSpan.FromSeconds(30));
            await snapshot.EnsureLoadedAsync(null, CancellationToken.None);
            await snapshot.ReloadInFlight;
        }
        var probesLoads = _loads;

        await snapshot.EnsureLoadedAsync("k1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        snapshot.Resolve("k1").Select(k => k.KeyId).Should().Contain(
            "k1", because: "the first request after setup must be accepted at once, not after a 30 s backoff");
        _loads.Should().Be(probesLoads + 1);
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldAcceptANewKeyId_WhenTheFirstTokenSignedWithItArrives()
    {
        var k1 = NewKey("k1");
        var k2 = NewKey("k2");
        Answer(k1);
        Answer(k1, k2);       // the pool after a rotation
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(null, CancellationToken.None);

        await snapshot.EnsureLoadedAsync("k2", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        snapshot.Resolve("k2").Select(k => k.KeyId).Should().Contain(
            "k2", because: "the first token signed with a rotated key must not be refused");
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldAcceptANewKeyIdWithinTheInterval_WhenForgedKeyIdsSpentTheReload()
    {
        var k1 = NewKey("k1");
        var k2 = NewKey("k2");
        Answer(k1);
        Answer(k1);           // the reload a forged kid triggers, read before the rotation
        Answer(k1, k2);
        var interval = TimeSpan.FromSeconds(2);
        var snapshot = NewSnapshot(new JwtValidationKeySnapshotOptions { UnknownKeyIdReloadInterval = interval });
        await snapshot.EnsureLoadedAsync(null, CancellationToken.None);

        var forged = snapshot.EnsureLoadedAsync("forged-1", CancellationToken.None);
        await snapshot.ReloadInFlight;
        _ = snapshot.EnsureLoadedAsync("forged-2", CancellationToken.None);
        _loads.Should().Be(2);

        // The rotated key exists now; the first token signed with it arrives inside the interval.
        var legit = snapshot.EnsureLoadedAsync("k2", CancellationToken.None);
        legit.IsCompleted.Should().BeFalse(because: "the request waits for the next reload instead of being refused");
        _clock.Advance(interval);
        await legit.WaitAsync(TimeSpan.FromSeconds(10));

        snapshot.Resolve("k2").Select(k => k.KeyId).Should().Contain(
            "k2", because: "forged key ids must not keep a real new key out beyond one interval");
        _loads.Should().Be(3);
        await forged.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldReloadAtMostOncePerInterval_WhenAFloodOfForgedKeyIdsArrives()
    {
        Answer(NewKey("k1"));
        var interval = TimeSpan.FromSeconds(2);
        var wait = TimeSpan.FromSeconds(5);
        var snapshot = NewSnapshot(new JwtValidationKeySnapshotOptions
        {
            UnknownKeyIdReloadInterval = interval,
            UnknownKeyIdWait = wait,
        });
        await snapshot.EnsureLoadedAsync(null, CancellationToken.None);
        var warm = _loads;

        var requests = new List<Task>();
        var step = TimeSpan.FromMilliseconds(250);
        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(20); t += step)
        {
            for (var i = 0; i < 25; i++)
                requests.Add(snapshot.EnsureLoadedAsync($"forged-{t.Ticks}-{i}", CancellationToken.None));
            await snapshot.ReloadInFlight;
            _clock.Advance(step);
        }
        _clock.Advance(wait);
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        (_loads - warm).Should().BeInRange(1, 11, because: "20 s of forged key ids cost one reload per 2 s interval at most");
        snapshot.Resolve(null).Select(k => k.KeyId).Should().Equal("k1");
    }
}
