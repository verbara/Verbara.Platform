using System.Diagnostics.Metrics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;

namespace Verbara.Platform.Core.Tests.Push;

public sealed class PlatformPushBusExtensionsTests
{
    // A replica's push bus at the 250-call target carries about 250 events a second (lab C.5, run
    // 2026-11-19-801d9eea: 88172 agentassist envelopes in a 5-minute hold, plus the replica's own events),
    // and its dispatch loop stalled for seconds while 250 agents signed in again on a 2-core replica.
    // Ten seconds of that rate, twice over, must wait in the buffer instead of evicting older events.
    private const int StallBurst = 5_000;

    [Fact]
    public async Task AddPlatformPushBus_ShouldDropNothing_WhenDispatchStallsThroughATargetLoadBurst()
    {
        using var provider = BuildProvider();
        var bus = provider.GetRequiredService<IPushEventBus>();
        var metrics = provider.GetRequiredService<PushMetrics>();
        using var drops = new DropCounter(metrics);
        using var gate = new ManualResetEventSlim(false);
        using var stalled = new ManualResetEventSlim(false);
        var received = 0;
        var expected = int.MaxValue;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = bus.AsObservable().Subscribe(new CallbackObserver(_ =>
        {
            if (!stalled.IsSet)
            {
                // The first event parks the dispatch loop, as a starved replica does.
                stalled.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            if (Interlocked.Increment(ref received) == Volatile.Read(ref expected))
                drained.TrySetResult();
        }));

        await bus.PublishAsync(new ProbeEvent(0));
        stalled.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the dispatch loop takes the first event");

        for (var i = 1; i <= StallBurst; i++)
            await bus.PublishAsync(new ProbeEvent(i));

        // The bus counts an eviction while the event is published, so every event it kept, and only those, reaches
        // the observer once the loop resumes.
        Volatile.Write(ref expected, (int)(StallBurst + 1 - drops.Count));
        gate.Set();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(30));

        drops.Count.Should().Be(0);
        Volatile.Read(ref received).Should().Be(StallBurst + 1);
    }

    [Fact]
    public void AddPlatformPushBus_ShouldUseTheDefaultCapacity_WhenNothingIsConfigured()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IOptions<PushEventBusOptions>>().Value.BufferCapacity
            .Should().Be(PlatformPushBusExtensions.DefaultBufferCapacity);
    }

    [Fact]
    public void AddPlatformPushBus_ShouldUseTheConfiguredCapacity_WhenTheKeyIsSet()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [PlatformPushBusExtensions.BufferCapacityKey] = "40000",
        });

        provider.GetRequiredService<IOptions<PushEventBusOptions>>().Value.BufferCapacity.Should().Be(40_000);
    }

    [Fact]
    public void AddPlatformPushBus_ShouldFailOnResolve_WhenTheValueIsNotAnInteger()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [PlatformPushBusExtensions.BufferCapacityKey] = "lots",
        });

        var read = () => provider.GetRequiredService<IOptions<PushEventBusOptions>>().Value;

        read.Should().Throw<InvalidOperationException>().WithMessage("*Push:Bus:BufferCapacity*lots*");
    }

    [Fact]
    public void AddPlatformPushBus_ShouldFailValidation_WhenTheValueIsBelowOne()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [PlatformPushBusExtensions.BufferCapacityKey] = "0",
        });

        var read = () => provider.GetRequiredService<IOptions<PushEventBusOptions>>().Value;

        read.Should().Throw<OptionsValidationException>();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (settings is not null)
        {
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        }

        services.AddPlatformPushBus();
        return services.BuildServiceProvider();
    }

    private sealed record ProbeEvent(int Sequence) : PushEvent
    {
        public override string EventType => "lab.probe";
    }

    private sealed class CallbackObserver(Action<PushEvent> onNext) : IObserver<PushEvent>
    {
        public void OnNext(PushEvent value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    /// <summary>Counts <c>asterisk.push.events.dropped</c> of this provider's bus only.</summary>
    private sealed class DropCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public DropCounter(PushMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.EventsDropped))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
