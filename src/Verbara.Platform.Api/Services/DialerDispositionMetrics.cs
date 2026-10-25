using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The dialer disposition hand-off's instruments (dialer-honours-dnc-callbacks-and-agent-dispositions, design D16), for
/// the operator: how hand-offs complete (by the dialer's answer), how many were released after a failure, and how many
/// dispositions stored typifications still owe the dialer. A <c>Pending</c> that does not close shows on the gauge
/// before the tenth-claim warning.
/// </summary>
/// <remarks>Built through an optional <see cref="IMeterFactory"/>, as <c>AutonomousDispositionMetrics</c>; owns its meter.</remarks>
public sealed class DialerDispositionMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> exported through OpenTelemetry (<c>Program.cs</c>).</summary>
    public const string MeterName = "verbara.platform.dialer";

    private readonly Meter _meter;
    private long _pending;

    /// <summary>Hand-offs the dialer answered, tagged <c>result</c> (an <c>AgentDispositionResult</c> name).</summary>
    public Counter<long> Completed { get; }

    /// <summary>Hand-offs released after an exception or a cancellation, to be handed over again.</summary>
    public Counter<long> Released { get; }

    public DialerDispositionMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        Completed = _meter.CreateCounter<long>(
            "dialer.disposition_dispatch.completed",
            description: "Agent dispositions the dialer answered, by result (design D16).");
        Released = _meter.CreateCounter<long>(
            "dialer.disposition_dispatch.released",
            description: "Agent disposition hand-offs released after a failure, to be handed over again (design D16).");
        _meter.CreateObservableGauge(
            "dialer.disposition_dispatch.pending",
            () => Interlocked.Read(ref _pending),
            description: "Agent dispositions stored typifications still owe the dialer (design D16).");
    }

    /// <summary>The last pending count the dispatch worker read.</summary>
    public long Pending => Interlocked.Read(ref _pending);

    public void RecordCompleted(string result) =>
        Completed.Add(1, new KeyValuePair<string, object?>("result", result));

    public void RecordReleased() => Released.Add(1);

    /// <summary>Sets the gauge (the dispatch worker, at each tick).</summary>
    public void SetPending(long count) => Interlocked.Exchange(ref _pending, count);

    public void Dispose() => _meter.Dispose();
}
