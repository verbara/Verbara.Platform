using Microsoft.Extensions.Configuration;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// <see cref="PasswordHashingGate"/> on its own (openspec <c>bound-concurrent-password-hashing</c>): never more
/// hashes at once than the bound, callers over it wait instead of failing, a cancelled wait takes no slot.
/// The endpoint half is <c>Auth/LoginHashingBoundTests</c>.
/// </summary>
public sealed class PasswordHashingGateTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunAsync_ShouldNeverRunMoreThanTheBound_WhenManyCallersArriveAtOnce()
    {
        using var gate = new PasswordHashingGate(2);
        var inFlight = 0;
        var peak = 0;
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);

        // Each caller on a thread of its own, so the hashes that hold a slot block their own thread
        // while the test holds them, never a pool thread the waiting callers' continuations need.
        var callers = Enumerable.Range(0, 12).Select(i => Task.Factory.StartNew(
            () => gate.RunAsync(() =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref inFlight));
                entered.Release();
                release.Wait(Deadline);
                Interlocked.Decrement(ref inFlight);
                return i;
            }, CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap()).ToArray();

        (await entered.WaitAsync(Deadline)).Should().BeTrue("the first caller takes a free slot");
        (await entered.WaitAsync(Deadline)).Should().BeTrue("the bound of two admits a second caller");
        (await entered.WaitAsync(Settle)).Should().BeFalse("both slots are held, so a third caller waits instead of hashing");
        Volatile.Read(ref inFlight).Should().Be(2);
        gate.AvailableSlots.Should().Be(0);

        release.Set();
        var results = await Task.WhenAll(callers).WaitAsync(Deadline);

        results.Should().BeEquivalentTo(Enumerable.Range(0, 12), "every caller over the bound waits and then runs");
        peak.Should().Be(2, "twelve callers arrived together against a bound of two");
        gate.AvailableSlots.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_ShouldWaitInsteadOfFailing_WhenEverySlotIsTaken()
    {
        using var gate = new PasswordHashingGate(1);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => gate.RunAsync(() => release.Wait(Deadline), CancellationToken.None));
        await WaitUntilAsync(() => gate.AvailableSlots == 0);

        var waiter = gate.RunAsync(() => 42, CancellationToken.None);
        await Task.Delay(Settle); // fence-allow: SETTLE — a negative check: give an unbounded run time to finish before asserting it has not

        waiter.IsCompleted.Should().BeFalse("the only slot is held, so the second caller waits");

        release.Set();
        (await waiter.WaitAsync(Deadline)).Should().Be(42);
        (await holder.WaitAsync(Deadline)).Should().BeTrue();
        gate.AvailableSlots.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ShouldThrowAndTakeNoSlot_WhenTheWaitIsCancelled()
    {
        using var gate = new PasswordHashingGate(1);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => gate.RunAsync(() => release.Wait(Deadline), CancellationToken.None));
        await WaitUntilAsync(() => gate.AvailableSlots == 0);

        using var cts = new CancellationTokenSource();
        var ran = false;
        var waiter = gate.RunAsync(() => ran = true, cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => waiter).Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();

        release.Set();
        await holder.WaitAsync(Deadline);
        gate.AvailableSlots.Should().Be(1, "a cancelled waiter never held the slot, so none leaks");
    }

    [Fact]
    public async Task RunAsync_ShouldFreeTheSlot_WhenTheWorkThrows()
    {
        using var gate = new PasswordHashingGate(1);

        await FluentActions.Awaiting(() => gate.RunAsync<int>(() => throw new InvalidOperationException("boom"), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        gate.AvailableSlots.Should().Be(1);
        (await gate.RunAsync(() => 7, CancellationToken.None)).Should().Be(7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Constructor_ShouldUseTheDefaultBound_WhenMaxConcurrencyIsNotPositive(int configured)
    {
        using var gate = new PasswordHashingGate(configured);

        gate.MaxConcurrency.Should().Be(PasswordHashingGate.DefaultMaxConcurrency);
        PasswordHashingGate.DefaultMaxConcurrency.Should().Be(Math.Max(1, Environment.ProcessorCount / 2));
    }

    [Fact]
    public void FromConfiguration_ShouldReadTheBound_WhenKeyIsSet()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PasswordHashingGate.MaxConcurrencyKey] = "3" })
            .Build();

        using var gate = PasswordHashingGate.FromConfiguration(configuration);

        gate.MaxConcurrency.Should().Be(3);
        gate.AvailableSlots.Should().Be(3);
    }

    [Fact]
    public void FromConfiguration_ShouldUseTheDefaultBound_WhenKeyIsAbsent()
    {
        using var gate = PasswordHashingGate.FromConfiguration(new ConfigurationBuilder().Build());

        gate.MaxConcurrency.Should().Be(PasswordHashingGate.DefaultMaxConcurrency);
    }

    [Fact]
    public async Task VerifyAsync_ShouldAgreeWithPasswordService_WhenPasswordMatchesOrNot()
    {
        using var gate = new PasswordHashingGate(1);
        var hash = await gate.HashAsync("Correct-Horse-1", CancellationToken.None);

        (await gate.VerifyAsync("Correct-Horse-1", hash, CancellationToken.None)).Should().BeTrue();
        (await gate.VerifyAsync("wrong", hash, CancellationToken.None)).Should().BeFalse();
        hash.Should().StartWith(Argon2idHasher.Prefix);
    }

    [Fact]
    public async Task EnterAsync_ShouldHoldTheSlotUntilDisposed_WhenDisposedTwice()
    {
        using var gate = new PasswordHashingGate(1);
        var hash = await gate.HashAsync("Correct-Horse-1", CancellationToken.None);

        var slot = await gate.EnterAsync(CancellationToken.None);
        gate.AvailableSlots.Should().Be(0);
        slot.Verify("Correct-Horse-1", hash).Should().BeTrue();
        slot.Verify("wrong", hash).Should().BeFalse();
        var waiting = gate.EnterAsync(CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse("the only slot is held until the first one is disposed");

        slot.Dispose();
        slot.Dispose();
        using (await waiting.WaitAsync(Deadline))
            gate.AvailableSlots.Should().Be(0, "a second dispose of the same slot frees nothing more");

        gate.AvailableSlots.Should().Be(1);
        FluentActions.Invoking(() => slot.Verify("Correct-Horse-1", hash)).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task EnterAsync_ShouldThrowAndTakeNoSlot_WhenTheWaitIsCancelled()
    {
        using var gate = new PasswordHashingGate(1);
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = gate.EnterAsync(cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        gate.AvailableSlots.Should().Be(0, "only the held slot is taken");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (current < value)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Deadline);
        while (!condition())
            await Task.Delay(10, cts.Token); // fence-allow: LOOP-DRIVER — bounded poll on the gate's slot count, which raises no signal of its own
    }
}
