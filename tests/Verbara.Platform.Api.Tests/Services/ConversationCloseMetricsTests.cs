using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// conversations-close-after-inactivity task 1.6 (design D13): every system close is counted on
/// <c>conversations.system_closed</c> of the meter <c>verbara.platform.conversations</c>, tagged by reason and by
/// nothing else (no tenant tag: tenant ids would make the series unbounded). The listener is scoped to this test's own
/// <see cref="IMeterFactory"/>, so parallel tests publishing the same meter name cannot leak in.
/// </summary>
public sealed class ConversationCloseMetricsTests
{
    [Fact]
    public void RecordClosed_ShouldAddOneTaggedByReasonOnly_WhenAnInactivityCloseIsRecorded()
    {
        using var harness = new Harness();

        harness.Metrics.RecordClosed(ConversationCloseReasons.Inactivity);

        var measurement = harness.Measurements.Should().ContainSingle().Subject;
        measurement.Meter.Should().Be("verbara.platform.conversations");
        measurement.Instrument.Should().Be("conversations.system_closed");
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().Equal(new Dictionary<string, string?> { ["reason"] = "inactivity" },
            "the reason is the only tag: no tenant tag");
    }

    [Fact]
    public void RecordClosed_ShouldKeepTheThreeReasonsAsDistinctSeries_WhenEachIsRecorded()
    {
        using var harness = new Harness();

        harness.Metrics.RecordClosed(ConversationCloseReasons.Inactivity);
        harness.Metrics.RecordClosed(ConversationCloseReasons.InactivityBacklog);
        harness.Metrics.RecordClosed(ConversationCloseReasons.InactivityBacklog);
        harness.Metrics.RecordClosed(ConversationCloseReasons.CustomerReturned);

        harness.Measurements
            .GroupBy(m => m.Tags["reason"])
            .ToDictionary(g => g.Key!, g => g.Sum(m => m.Value))
            .Should().Equal(new Dictionary<string, long>
            {
                ["inactivity"] = 1,
                ["inactivity-backlog"] = 2,
                ["customer-returned"] = 1,
            });
    }

    [Fact]
    public void ConversationCloseMetrics_ShouldBeRegisteredOnceAndExported_WhenTheHostStarts()
    {
        using var factory = new PlatformApiFactory();

        var first = factory.Services.GetRequiredService<ConversationCloseMetrics>();
        var second = factory.Services.GetRequiredService<ConversationCloseMetrics>();

        first.Should().BeSameAs(second, "one singleton owns the meter");
        ConversationCloseMetrics.MeterName.Should().Be("verbara.platform.conversations");
    }

    private sealed record Measurement(string Meter, string Instrument, long Value, Dictionary<string, string?> Tags);

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public Harness()
        {
            _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var meterFactory = _services.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var tagMap = new Dictionary<string, string?>();
                foreach (var tag in tags)
                    tagMap[tag.Key] = tag.Value?.ToString();
                _measurements.Enqueue(new Measurement(instrument.Meter.Name, instrument.Name, value, tagMap));
            });
            _listener.Start();
            Metrics = new ConversationCloseMetrics(meterFactory);
        }

        public ConversationCloseMetrics Metrics { get; }

        public IReadOnlyList<Measurement> Measurements => [.. _measurements];

        public void Dispose()
        {
            _listener.Dispose();
            Metrics.Dispose();
            _services.Dispose();
        }
    }
}
