using Verbara.Platform.Api.Services;
using Verbara.Sdk;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// #385 — <see cref="TrunkRegistrationReloadWorker"/> turns the in-memory reload signal into exactly one AMI
/// <c>ModuleLoad reload res_pjsip_outbound_registration.so</c>, keeps the signal dirty and retries on every
/// failure, never throws, and runs a leader-gated convergence pass over the <c>ps_registrations</c>
/// fingerprint. Harness style of <see cref="TrunkConnectivityTesterTests"/>.
/// </summary>
public sealed class TrunkRegistrationReloadWorkerTests : IDisposable
{
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
    private readonly TrunkRegistrationReloadSignal _signal = new();
    private readonly IClusterLeader _leader = Substitute.For<IClusterLeader>();
    private readonly ITrunkRegistrationFingerprintSource _fingerprints = Substitute.For<ITrunkRegistrationFingerprintSource>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private int _sends;

    public TrunkRegistrationReloadWorkerTests()
    {
        _ami.State.Returns(AmiConnectionState.Connected);
        AnswerWith("Success");
        _leader.IsLeader.Returns(true);
        _leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).Returns(default(string));
    }

    public void Dispose() => _serverPool.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void AnswerWith(params string[] responses)
    {
        var queue = new Queue<string>(responses);
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref _sends);
                var response = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return new ManagerResponse
                {
                    Response = response,
                    Message = response == "Success" ? "Module reloaded." : "Module not found.",
                };
            });
    }

    private TrunkRegistrationReloadWorker CreateWorker(bool withServer = true, int intervalSeconds = 3600)
    {
        if (withServer)
            _serverPool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        return new TrunkRegistrationReloadWorker(
            _signal, _serverPool, _leader, _fingerprints,
            Options.Create(new RealtimeOptions { ReconcilerIntervalSeconds = intervalSeconds }),
            NullLogger<TrunkRegistrationReloadWorker>.Instance, _clock);
    }

    private async Task ReceivedReloads(int count) =>
        await _ami.Received(count).SendActionAsync(
            Arg.Is<ManagerAction>(a => a is ModuleLoadAction
                && ((ModuleLoadAction)a).Module == "res_pjsip_outbound_registration.so"
                && ((ModuleLoadAction)a).LoadType == "reload"),
            Arg.Any<CancellationToken>());

    /// <summary>Advances fake time in small steps, yielding real time so the loop's continuations run.</summary>
    private async Task AdvanceAsync(TimeSpan total, Func<bool>? until = null)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            if (until?.Invoke() == true)
                return;
            _clock.Advance(step);
            await Task.Delay(15); // fence-allow: LOOP-DRIVER — poll pacing so the loop can arm its next fake-time delay before the next step
        }
    }

    // ─── Signal path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReloadOnceAsync_ShouldSendOneModuleReload_WhenSignalled()
    {
        var sut = CreateWorker();
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeTrue();

        await ReceivedReloads(1);
        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldSendNothing_WhenNotSignalled()
    {
        var sut = CreateWorker();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeTrue();

        _sends.Should().Be(0);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldSendOneAction_WhenSignalledSeveralTimes()
    {
        var sut = CreateWorker();
        _signal.RequestReload();
        _signal.RequestReload();
        _signal.RequestReload();

        await sut.ReloadOnceAsync(CancellationToken.None);
        await sut.ReloadOnceAsync(CancellationToken.None);

        await ReceivedReloads(1);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldStayDirty_WhenAsteriskAnswersError()
    {
        AnswerWith("Error");
        var sut = CreateWorker();
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeFalse();
        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeFalse();

        _signal.IsDirty.Should().BeTrue();
        await ReceivedReloads(2);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldNotThrowAndStayDirty_WhenNoPrimaryServer()
    {
        var sut = CreateWorker(withServer: false);
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeFalse();

        _signal.IsDirty.Should().BeTrue();
        _sends.Should().Be(0);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldStayDirty_WhenConnectionIsNotConnected()
    {
        _ami.State.Returns(AmiConnectionState.Reconnecting);
        var sut = CreateWorker();
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeFalse();

        _signal.IsDirty.Should().BeTrue();
        _sends.Should().Be(0);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldSwallowAndStayDirty_WhenSendThrows()
    {
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmiNotConnectedException());
        var sut = CreateWorker();
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeFalse();

        _signal.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldReload_WhenNotLeader()
    {
        _leader.IsLeader.Returns(false);
        var sut = CreateWorker();
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeTrue();

        await ReceivedReloads(1);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldRecordFingerprint_SoTheNextTickDoesNotReloadAgain()
    {
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).Returns("1:abc");
        var sut = CreateWorker();
        _signal.RequestReload();

        await sut.ReloadOnceAsync(CancellationToken.None);
        await sut.TickAsync(CancellationToken.None);

        await ReceivedReloads(1);
    }

    [Fact]
    public async Task ReloadOnceAsync_ShouldStillReload_WhenFingerprintReadThrows()
    {
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        var sut = CreateWorker();
        _signal.RequestReload();

        (await sut.ReloadOnceAsync(CancellationToken.None)).Should().BeTrue();

        await ReceivedReloads(1);
    }

    // ─── Periodic convergence path ────────────────────────────────────────────

    [Fact]
    public async Task TickAsync_ShouldNotReload_WhenNotLeader()
    {
        _leader.IsLeader.Returns(false);
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).Returns("1:abc");
        var sut = CreateWorker();

        await sut.TickAsync(CancellationToken.None);

        _sends.Should().Be(0);
        await _fingerprints.DidNotReceive().GetFingerprintAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TickAsync_ShouldReloadOnlyWhenFingerprintChanges()
    {
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).Returns("1:abc", "1:abc", "2:def");
        var sut = CreateWorker();

        await sut.TickAsync(CancellationToken.None);   // none recorded yet → reload
        await ReceivedReloads(1);

        await sut.TickAsync(CancellationToken.None);   // same → nothing
        await ReceivedReloads(1);

        await sut.TickAsync(CancellationToken.None);   // different → reload
        await ReceivedReloads(2);
    }

    [Fact]
    public async Task TickAsync_ShouldNotReload_WhenFingerprintUnavailable()
    {
        var sut = CreateWorker();

        await sut.TickAsync(CancellationToken.None);

        _sends.Should().Be(0);
    }

    [Fact]
    public async Task TickAsync_ShouldRetryOnNextTick_WhenReloadFails()
    {
        AnswerWith("Error", "Success");
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).Returns("1:abc");
        var sut = CreateWorker();

        await sut.TickAsync(CancellationToken.None);
        await sut.TickAsync(CancellationToken.None);
        await sut.TickAsync(CancellationToken.None);

        await ReceivedReloads(2);
    }

    [Fact]
    public async Task TickAsync_ShouldNotThrow_WhenFingerprintReadThrows()
    {
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        var sut = CreateWorker();

        var act = async () => await sut.TickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _sends.Should().Be(0);
    }

    // ─── The running loop ────────────────────────────────────────────────────

    [Fact]
    public async Task Loop_ShouldSendOneAction_WhenSignalsArriveWithinTheDebounce()
    {
        _leader.IsLeader.Returns(false);
        using var sut = CreateWorker();
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // fence-allow: SETTLE — let ExecuteAsync (off the StartAsync caller) reach its first fake-time wait; later steps tolerate a late arm

        _signal.RequestReload();
        await AdvanceAsync(TimeSpan.FromMilliseconds(500));
        _signal.RequestReload();
        await AdvanceAsync(TimeSpan.FromMilliseconds(500));
        _signal.RequestReload();
        await AdvanceAsync(TimeSpan.FromMilliseconds(500));
        _sends.Should().Be(0, "every signal restarts the quiet window");

        await AdvanceAsync(TimeSpan.FromSeconds(3), until: () => Volatile.Read(ref _sends) > 0);
        await AdvanceAsync(TimeSpan.FromSeconds(3));
        await sut.StopAsync(CancellationToken.None);

        await ReceivedReloads(1);
        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Loop_ShouldRetryWithBackoff_UntilAsteriskAcceptsTheReload()
    {
        AnswerWith("Error", "Error", "Success");
        _leader.IsLeader.Returns(false);
        using var sut = CreateWorker();
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // fence-allow: SETTLE — let ExecuteAsync (off the StartAsync caller) reach its first fake-time wait; later steps tolerate a late arm

        _signal.RequestReload();
        await AdvanceAsync(TimeSpan.FromSeconds(1.5), until: () => Volatile.Read(ref _sends) > 0);
        _sends.Should().Be(1);
        await AdvanceAsync(TimeSpan.FromMilliseconds(1500));
        _sends.Should().Be(1, "the first retry waits a 1 s backoff and then the 1 s debounce");

        await AdvanceAsync(TimeSpan.FromSeconds(10), until: () => Volatile.Read(ref _sends) >= 3);
        await AdvanceAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        await ReceivedReloads(3);
        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Loop_ShouldRunTheConvergencePassEveryInterval()
    {
        _fingerprints.GetFingerprintAsync(Arg.Any<CancellationToken>()).Returns("1:abc", "1:abc", "2:def");
        using var sut = CreateWorker(intervalSeconds: 5);
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // fence-allow: SETTLE — let ExecuteAsync (off the StartAsync caller) reach its first fake-time wait; later steps tolerate a late arm

        await AdvanceAsync(TimeSpan.FromSeconds(6), until: () => Volatile.Read(ref _sends) > 0);
        _sends.Should().Be(1);
        await AdvanceAsync(TimeSpan.FromSeconds(5.5));
        _sends.Should().Be(1, "the fingerprint did not change");
        await AdvanceAsync(TimeSpan.FromSeconds(6), until: () => Volatile.Read(ref _sends) > 1);
        await sut.StopAsync(CancellationToken.None);

        await ReceivedReloads(2);
    }

    [Fact]
    public async Task FingerprintSource_ShouldAnswerNull_WhenNoRealtimeDatabaseIsConfigured()
    {
        var source = new RealtimeTrunkRegistrationFingerprintSource(null);

        (await source.GetFingerprintAsync(CancellationToken.None)).Should().BeNull();
    }
}
