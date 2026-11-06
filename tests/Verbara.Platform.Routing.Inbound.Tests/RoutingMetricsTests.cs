using System.Diagnostics.Metrics;

namespace Verbara.Platform.Routing.Inbound.Tests;

/// <summary>
/// conversations-return-to-last-agent task 1.5 (design D8): the counter's names are the metric fixture's, verbatim —
/// meter <c>verbara.platform.routing</c>, counter <c>routing.last_agent</c>, unit <c>{decision}</c>, one tag
/// <c>outcome</c> with the seven values — read back through a <see cref="MeterListener"/>, as an exporter sees them.
/// </summary>
public sealed class RoutingMetricsTests
{
    private static readonly string[] FixtureOutcomes =
        ["disabled", "no-history", "skipped-identity", "lookup-failed", "stamped", "offered-preferred", "fallback"];

    [Fact]
    public void RecordLastAgent_ShouldPublishTheFixtureNames_WhenEachOutcomeIsCounted()
    {
        using var metrics = new RoutingMetrics();
        var seen = new List<(string Meter, string Instrument, string? Unit, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.LastAgent.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (seen)
                seen.Add((instrument.Meter.Name, instrument.Name, instrument.Unit, value, tags.ToArray()));
        });
        listener.Start();

        foreach (var outcome in new[]
                 {
                     LastAgentOutcomes.Disabled, LastAgentOutcomes.NoHistory, LastAgentOutcomes.SkippedIdentity,
                     LastAgentOutcomes.LookupFailed, LastAgentOutcomes.Stamped, LastAgentOutcomes.OfferedPreferred,
                     LastAgentOutcomes.Fallback,
                 })
            metrics.RecordLastAgent(outcome);

        seen.Should().HaveCount(7);
        seen.Should().OnlyContain(m => m.Meter == "verbara.platform.routing" && m.Instrument == "routing.last_agent"
                                       && m.Unit == "{decision}" && m.Value == 1);
        seen.Should().OnlyContain(m => m.Tags.Length == 1 && m.Tags[0].Key == "outcome", "one tag, no tenant tag");
        seen.Select(m => (string)m.Tags[0].Value!).Should().Equal(FixtureOutcomes);
        RoutingMetrics.MeterName.Should().Be("verbara.platform.routing");
    }
}
