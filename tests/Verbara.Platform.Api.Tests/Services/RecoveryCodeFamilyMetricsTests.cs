using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// The census half of <see cref="RecoveryCodeFamilyMetrics"/> (openspec <c>harden-recovery-code-digest</c>,
/// design D6): the gauge reports the last successful census, keeps it when a census fails, and the census
/// seam is never resolved at host start. Time is a <see cref="FakeTimeProvider"/>; the meter is read through
/// a <see cref="MeterListener"/> scoped to this test's own <see cref="IMeterFactory"/>, so parallel tests
/// publishing the same meter name cannot leak in.
/// </summary>
public sealed class RecoveryCodeFamilyMetricsTests
{
    private static readonly RecoveryCodeCensusOptions Options =
        new(InitialDelay: TimeSpan.FromMinutes(2), Interval: TimeSpan.FromMinutes(60));

    [Fact]
    public async Task ExecuteAsync_ShouldPublishCensusOnGauge_WhenFirstTickSucceeds()
    {
        var census = new ScriptedCensus(_ => new RecoveryCodeCensus(UsersOnArgon2id: 7, UsersOnLegacy: 3));
        await using var harness = Harness.Create(census);

        await harness.Metrics.StartAsync(CancellationToken.None);
        harness.ReadGauge().Should().BeEmpty(because: "until the first census succeeds the gauge reports nothing");

        await harness.AdvanceUntilCallAsync(census, 1);

        var gauge = await harness.WaitForGaugeAsync();
        gauge.Should().BeEquivalentTo(new Dictionary<string, long> { ["argon2id"] = 7, ["legacy"] = 3 });

        await harness.Metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepLastValue_WhenCensusThrows()
    {
        var census = new ScriptedCensus(call => call == 1
            ? new RecoveryCodeCensus(UsersOnArgon2id: 5, UsersOnLegacy: 2)
            : throw new InvalidOperationException("database unavailable"));
        await using var harness = Harness.Create(census);

        await harness.Metrics.StartAsync(CancellationToken.None);
        await harness.AdvanceUntilCallAsync(census, 1);
        (await harness.WaitForGaugeAsync()).Should().ContainKey("legacy");

        // Call 2 throws. Call 3 starting proves call 2's failure was fully handled.
        await harness.AdvanceUntilCallAsync(census, 3);

        harness.ReadGauge().Should().BeEquivalentTo(
            new Dictionary<string, long> { ["argon2id"] = 5, ["legacy"] = 2 },
            because: "a failed census is logged and the previous value kept");

        await harness.Metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldNotResolveCensus_WhenHostStarts()
    {
        // Deferred-seam rule: a host that boots without a database must not
        // crash on the census, so the seam is resolved at the first tick, never during start.
        var census = new ScriptedCensus(_ => new RecoveryCodeCensus(0, 0));
        await using var harness = Harness.Create(census);

        await harness.Metrics.StartAsync(CancellationToken.None);
        // ExecuteAsync runs on its own thread; give it time to reach its first delay so a worker that
        // resolved the seam before waiting would be caught.
        await Task.Delay(200); // fence-allow: SETTLE — let ExecuteAsync (off the caller's thread since .NET 10) reach its first delay; a mutant resolving the seam first is caught

        harness.CensusResolutions.Should().Be(0,
            because: "StartAsync must not resolve IRecoveryCodeCensus; the first tick does");

        await harness.Metrics.StopAsync(CancellationToken.None);
    }

    /// <summary>A census whose result per call (1-based) is scripted, and that signals each call.</summary>
    private sealed class ScriptedCensus(Func<int, RecoveryCodeCensus> script) : IRecoveryCodeCensus
    {
        private readonly Lock _gate = new();
        private readonly List<TaskCompletionSource> _calls = [];
        private int _count;

        public int Calls
        {
            get
            {
                lock (_gate)
                    return _count;
            }
        }

        public Task<RecoveryCodeCensus> CountAsync(CancellationToken ct)
        {
            int call;
            lock (_gate)
            {
                call = ++_count;
                Signal(call).TrySetResult();
            }

            return Task.FromResult(script(call));
        }

        public Task WaitForCallAsync(int call)
        {
            lock (_gate)
                return Signal(call).Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private TaskCompletionSource Signal(int call)
        {
            while (_calls.Count < call)
                _calls.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return _calls[call - 1];
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _gauge = new(StringComparer.Ordinal);
        private int _resolutions;

        private Harness(ScriptedCensus census)
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<IRecoveryCodeCensus>(_ =>
            {
                Interlocked.Increment(ref _resolutions);
                return census;
            });
            _provider = services.BuildServiceProvider();

            var meterFactory = _provider.GetRequiredService<IMeterFactory>();
            Metrics = new RecoveryCodeFamilyMetrics(
                _provider, meterFactory, Options, Time, NullLogger<RecoveryCodeFamilyMetrics>.Instance);

            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RecoveryCodeFamilyMetrics.MeterName
                    && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Name == "mfa.recovery_codes.users")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "family")
                        _gauge[(string)tag.Value!] = value;
                }
            });
            _listener.Start();
        }

        public FakeTimeProvider Time { get; } = new();

        public RecoveryCodeFamilyMetrics Metrics { get; }

        public int CensusResolutions => Volatile.Read(ref _resolutions);

        public static Harness Create(ScriptedCensus census) => new(census);

        /// <summary>
        /// Advance one interval at a time until the census has been called <paramref name="call"/> times. The
        /// worker arms each delay on its own thread (ExecuteAsync runs off the StartAsync caller since .NET 10,
        /// and the next delay only after a tick finishes), so a single advance could land before the timer
        /// exists; stepping until the call is observed removes that race.
        /// </summary>
        public async Task AdvanceUntilCallAsync(ScriptedCensus census, int call)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (census.Calls < call && DateTime.UtcNow < deadline)
            {
                Time.Advance(Options.Interval);
                await Task.Delay(10); // fence-allow: LOOP-DRIVER — poll pacing until the worker has armed its next fake-time delay
            }

            await census.WaitForCallAsync(call);
        }

        public Dictionary<string, long> ReadGauge()
        {
            _gauge.Clear();
            _listener.RecordObservableInstruments();
            return new Dictionary<string, long>(_gauge, StringComparer.Ordinal);
        }

        /// <summary>The census result is published just after the call returns; poll briefly for it.</summary>
        public async Task<Dictionary<string, long>> WaitForGaugeAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var gauge = ReadGauge();
                if (gauge.Count > 0)
                    return gauge;
                await Task.Delay(10); // fence-allow: LOOP-DRIVER — poll pacing until the census result is published after the call returns
            }

            return ReadGauge();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            Metrics.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
