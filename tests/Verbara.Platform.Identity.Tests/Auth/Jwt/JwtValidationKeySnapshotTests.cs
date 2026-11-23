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

    // ─── A reload that has (or has not) read the pool when a new key id arrives ──────────────────

    [Fact]
    public async Task EnsureLoadedAsync_ShouldAcceptANewKeyId_WhenTheKeyLandsWhileAReloadThatAlreadyReadThePoolRuns()
    {
        // A refresh reads the pool, then its answer is slow to come back (a slow Redis round trip).
        // The rotation lands after that read, and the first token signed with the new key arrives
        // while the refresh still runs: that refresh can never return the new key, so the request
        // must wait for the next reload, started once the running one is done, not join it.
        var pool = new GatedPool();
        pool.Put(NewKey("k1"));
        var unknownKeyIdReloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new JwtValidationKeySnapshot(
            pool.LoadAsync,
            Build,
            timeProvider: _clock,
            events: new JwtValidationKeySnapshotEvents { UnknownKeyIdReload = () => unknownKeyIdReloadStarted.TrySetResult() });
        await snapshot.EnsureLoadedAsync(null, CancellationToken.None);

        pool.HoldAnswers();
        _clock.Advance(TimeSpan.FromMinutes(6));
        _ = snapshot.Resolve(null);                                  // the refresh starts and reads k1 only
        await pool.HasRead.WaitAsync(TimeSpan.FromSeconds(10));
        pool.Put(NewKey("k2"));

        var legit = snapshot.EnsureLoadedAsync("k2", CancellationToken.None);
        // A reload asked for now that does not wait for the running one joins it, and announces
        // itself at once; give it that chance before the running reload answers. On the correct
        // path nothing starts here, so this wait simply runs out.
        await Task.WhenAny(unknownKeyIdReloadStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(500))); // fence-allow: GUARD-TIMEOUT — bounds the wait for a reload that must NOT start while the running one holds; its absence is the correct path
        legit.IsCompleted.Should().BeFalse(because: "the request waits for a reload that reads the pool after the new key landed");
        pool.ReleaseAnswers();
        await legit.WaitAsync(TimeSpan.FromSeconds(10));

        snapshot.Resolve("k2").Select(k => k.KeyId).Should().Contain(
            "k2", because: "a request must never settle for a reload that read the pool before its key existed");
        pool.Loads.Should().Be(3, because: "the first load, the slow refresh, and one reload for the new key id");
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldJoinTheReload_WhenItHasNotReadThePoolYet()
    {
        // A reload that has started but not read the pool yet will read it after this request
        // arrived: sharing it costs nothing and answers the request, so no second load runs.
        var pool = new GatedPool();
        pool.Put(NewKey("k1"));
        using var hold = new ManualResetEventSlim(initialState: true);
        TaskCompletionSource? reloadStarted = null;
        var snapshot = new JwtValidationKeySnapshot(
            pool.LoadAsync,
            Build,
            timeProvider: _clock,
            events: new JwtValidationKeySnapshotEvents
            {
                // Runs on the reload's thread before it reads the pool: holding it here keeps the
                // reload started but unread.
                ReloadStarted = () =>
                {
                    Volatile.Read(ref reloadStarted)?.TrySetResult();
                    hold.Wait(TimeSpan.FromSeconds(10));
                },
            });
        await snapshot.EnsureLoadedAsync(null, CancellationToken.None);

        hold.Reset();
        Volatile.Write(ref reloadStarted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _clock.Advance(TimeSpan.FromMinutes(6));
        _ = snapshot.Resolve(null);
        await reloadStarted!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pool.Put(NewKey("k2"));

        var legit = snapshot.EnsureLoadedAsync("k2", CancellationToken.None);
        hold.Set();
        await legit.WaitAsync(TimeSpan.FromSeconds(10));

        snapshot.Resolve("k2").Select(k => k.KeyId).Should().Contain("k2");
        pool.Loads.Should().Be(2, because: "the request shares the reload that had not read the pool yet instead of starting another");
    }

    [Fact]
    public async Task Resolve_ShouldNotReload_WhenTheTokenNamesAHeldKeyOrNone()
    {
        Answer(NewKey("k1"));
        var snapshot = NewSnapshot();
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            snapshot.Resolve("k1").Select(k => k.KeyId).Should().Equal("k1");
            snapshot.Resolve(null).Select(k => k.KeyId).Should().Equal("k1");
            snapshot.Resolve("").Select(k => k.KeyId).Should().Equal("k1");
        }
        await snapshot.ReloadInFlight.WaitAsync(TimeSpan.FromSeconds(10));

        _loads.Should().Be(1, because: "only a key id the snapshot does not hold asks for a reload");
        snapshot.PendingUnknownKeyIdReload.IsCompleted.Should().BeTrue(because: "nor schedules one");
    }

    private static SymmetricSecurityKey Build(JwtKeyEntry entry) =>
        new SymmetricSecurityKey(Convert.FromBase64String(entry.Key)) { KeyId = entry.KeyId };

    /// <summary>
    /// A key pool each load reads when it is called; while <see cref="HoldAnswers"/> is in force the
    /// load then waits before answering what it read, as a slow store round trip does.
    /// </summary>
    private sealed class GatedPool
    {
        private readonly Lock _lock = new();
        private readonly List<JwtKeyEntry> _entries = [];
        private TaskCompletionSource? _answers;
        private TaskCompletionSource _hasRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);

        /// <summary>Completes when a load has read the pool since the last <see cref="HoldAnswers"/>.</summary>
        public Task HasRead => Volatile.Read(ref _hasRead).Task;

        public void Put(JwtKeyEntry entry)
        {
            lock (_lock)
                _entries.Add(entry);
        }

        public void HoldAnswers()
        {
            Volatile.Write(ref _hasRead, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _answers, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public void ReleaseAnswers() => Volatile.Read(ref _answers)?.TrySetResult();

        public async Task<IReadOnlyList<JwtKeyEntry>> LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            JwtKeyEntry[] read;
            lock (_lock)
                read = [.. _entries];
            var answers = Volatile.Read(ref _answers);
            Volatile.Read(ref _hasRead).TrySetResult();
            if (answers is not null)
                await answers.Task.ConfigureAwait(false);
            return read;
        }
    }
}
