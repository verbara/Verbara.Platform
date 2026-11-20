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
    public async Task EnsureLoadedAsync_ShouldRetryAfterTheBackoff_WhenTheFirstLoadReturnsNoKey()
    {
        // A pool read that silently skips a disconnected server answers with no key. Cached as a
        // loaded set, it would refuse every token until the next refresh, 5 minutes later.
        Answer();
        Answer(NewKey("k1"));
        var snapshot = NewSnapshot();

        await snapshot.EnsureLoadedAsync(CancellationToken.None);
        snapshot.IsLoaded.Should().BeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        await snapshot.EnsureLoadedAsync(CancellationToken.None);

        snapshot.IsLoaded.Should().BeTrue();
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

        _clock.Advance(TimeSpan.FromSeconds(9));
        _ = snapshot.Resolve("unknown");
        _loads.Should().Be(2);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _ = snapshot.Resolve("unknown");
        await snapshot.ReloadInFlight;
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
}
