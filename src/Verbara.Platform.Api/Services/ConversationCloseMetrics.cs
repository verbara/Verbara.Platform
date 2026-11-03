using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// conversations-close-after-inactivity (design D13): the system-close counter, for the operator. Every won system
/// close adds one to <c>conversations.system_closed</c>, tagged <c>reason</c> = <c>inactivity</c>,
/// <c>inactivity-backlog</c> or <c>customer-returned</c> (Prometheus <c>conversations_system_closed_total{reason}</c>).
/// Only a winner records: the inactivity pass after a won conditional close, the customer-return announcer after a
/// won return close. No tenant tag: tenant ids would make the series unbounded.
/// </summary>
/// <remarks>Built through an optional <see cref="IMeterFactory"/>, as <see cref="DialerDispositionMetrics"/>; owns its meter.</remarks>
public sealed class ConversationCloseMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> exported through OpenTelemetry (<c>Program.cs</c>).</summary>
    public const string MeterName = "verbara.platform.conversations";

    private readonly Meter _meter;

    /// <summary>System closes, tagged <c>reason</c>.</summary>
    public Counter<long> SystemClosed { get; }

    public ConversationCloseMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        SystemClosed = _meter.CreateCounter<long>(
            "conversations.system_closed",
            description: "Conversations closed by the system, by reason: inactivity, inactivity-backlog, customer-returned (design D13).");
    }

    /// <summary>Counts one won system close of <paramref name="reason"/>.</summary>
    public void RecordClosed(string reason) =>
        SystemClosed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose() => _meter.Dispose();
}
