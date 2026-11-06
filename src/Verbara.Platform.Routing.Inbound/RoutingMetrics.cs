using System.Diagnostics.Metrics;

namespace Verbara.Platform.Routing.Inbound;

/// <summary>
/// conversations-return-to-last-agent (design D8): the last-agent routing counter, for the operator and the lab cell.
/// Each routing attempt that returned a route adds one routing-stage outcome (recorded by <c>LastAgentMiddleware</c>);
/// each first offer of a conversation that carried a preference adds one distribution-stage outcome (recorded by the
/// switchboard after its offer-state write commits). Counter <c>routing.last_agent</c> on meter
/// <c>verbara.platform.routing</c>, tagged <c>outcome</c> (Prometheus <c>routing_last_agent_total{outcome}</c>). No tenant
/// tag: tenant ids would make the series unbounded.
/// </summary>
/// <remarks>Built through an optional <see cref="IMeterFactory"/>, as <c>ConversationCloseMetrics</c>; owns its meter.</remarks>
public sealed class RoutingMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> exported through OpenTelemetry (<c>Program.cs</c>).</summary>
    public const string MeterName = "verbara.platform.routing";

    /// <summary>Name of the counter.</summary>
    public const string LastAgentCounterName = "routing.last_agent";

    /// <summary>Name of the counter's one tag.</summary>
    public const string OutcomeTag = "outcome";

    private readonly Meter _meter;

    /// <summary>Last-agent routing decisions, tagged <see cref="OutcomeTag"/> with a <see cref="LastAgentOutcomes"/> value.</summary>
    public Counter<long> LastAgent { get; }

    public RoutingMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        LastAgent = _meter.CreateCounter<long>(
            LastAgentCounterName,
            unit: "{decision}",
            description: "Last-agent routing decisions by outcome: disabled, no-history, skipped-identity, lookup-failed, " +
                         "stamped (routing stage), offered-preferred, fallback (distribution stage).");
    }

    /// <summary>Counts one decision of <paramref name="outcome"/>, a <see cref="LastAgentOutcomes"/> value.</summary>
    public void RecordLastAgent(string outcome) =>
        LastAgent.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));

    public void Dispose() => _meter.Dispose();
}

/// <summary>The values of the <c>outcome</c> tag of <c>routing.last_agent</c> (design D8).</summary>
public static class LastAgentOutcomes
{
    /// <summary>Routing stage: the feature is off for the route's final queue.</summary>
    public const string Disabled = "disabled";

    /// <summary>Routing stage: no qualifying previous conversation within the window.</summary>
    public const string NoHistory = "no-history";

    /// <summary>Routing stage: a shared placeholder contact, or a WebChat contact that is not verified (design D7).</summary>
    public const string SkippedIdentity = "skipped-identity";

    /// <summary>Routing stage: the lookup threw; the route continued with no preference (design D6).</summary>
    public const string LookupFailed = "lookup-failed";

    /// <summary>Routing stage: the route carries a preferred agent.</summary>
    public const string Stamped = "stamped";

    /// <summary>Distribution stage: the first offer went to the preferred agent.</summary>
    public const string OfferedPreferred = "offered-preferred";

    /// <summary>Distribution stage: the first offer went to another agent.</summary>
    public const string Fallback = "fallback";
}
