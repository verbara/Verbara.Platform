using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Owns the <c>verbara.platform.typification.ai</c> <see cref="Meter"/> and all instruments
/// emitted by the AI typification classification path (B2+).
/// </summary>
/// <remarks>
/// Follows the house pattern from <c>LlmMetrics</c>: constructed via an optional
/// <see cref="IMeterFactory"/> (production DI path) with a plain <c>new Meter(...)</c>
/// fallback so unit-test constructors that omit the factory still compile and run.
/// <para>
/// An optional <see cref="ILoggerFactory"/> is accepted so that the singleton can own a
/// single cached <see cref="ILogger"/> — avoiding per-request logger creation that would
/// trigger CA1873 at the call site.
/// </para>
/// <para>
/// <strong>Disposal:</strong> this type owns and explicitly disposes its <see cref="Meter"/>
/// to prevent leaks in test or recycled-DI scenarios.
/// </para>
/// <para>
/// Counters <c>suggestion.accepted</c> and <c>suggestion.overridden</c> are reserved here
/// but not yet incremented — they will be wired in task B3 (provenance reconciliation).
/// </para>
/// </remarks>
public sealed partial class TypificationAiMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> registered with OpenTelemetry.</summary>
    public const string MeterName = "verbara.platform.typification.ai";

    private readonly Meter _meter;
    private readonly ILogger _logger;

    // ─── Instruments ────────────────────────────────────────────────────────────

    /// <summary>
    /// Total AI typification suggestions persisted (incremented regardless of AiMode).
    /// </summary>
    public Counter<long> SuggestionMade { get; }

    /// <summary>
    /// Suggestions accepted verbatim by an agent (B3 — reserved, not yet incremented).
    /// </summary>
    public Counter<long> SuggestionAccepted { get; }

    /// <summary>
    /// Suggestions where the agent chose a different leaf (B3 — reserved, not yet incremented).
    /// </summary>
    public Counter<long> SuggestionOverridden { get; }

    public TypificationAiMetrics(IMeterFactory? meterFactory = null, ILoggerFactory? loggerFactory = null)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        _logger = loggerFactory?.CreateLogger("Verbara.Platform.Typification.Ai")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger("Verbara.Platform.Typification.Ai");

        SuggestionMade = _meter.CreateCounter<long>(
            "suggestion.made",
            description: "Total AI typification suggestions persisted (all modes, including Shadow).");

        SuggestionAccepted = _meter.CreateCounter<long>(
            "suggestion.accepted",
            description: "AI typification suggestions accepted verbatim by the agent (reconciled in B3).");

        SuggestionOverridden = _meter.CreateCounter<long>(
            "suggestion.overridden",
            description: "AI typification suggestions where the agent chose a different leaf (reconciled in B3).");
    }

    // ─── Instance log helper (uses pre-cached logger) ───────────────────────────

    /// <summary>
    /// Emits EventId 6310 "suggestion persisted" at Debug level using the pre-cached logger.
    /// </summary>
    /// <remarks>
    /// <strong>Intentional deviation from <c>LlmMetrics</c>:</strong> <c>LlmMetrics</c> exposes
    /// <c>internal static partial</c> <c>[LoggerMessage]</c> methods and the consumer owns the
    /// <see cref="ILogger"/>. Here the metrics class itself owns the logger (resolved once from
    /// <c>ILoggerFactory</c> in the ctor) and wraps the source-generated static core. This is the
    /// natural choice because <c>TypificationAiMetrics</c> is a DI singleton (the obvious owner of a
    /// single cached logger) and it keeps the minimal-API handler from taking yet another
    /// <c>[FromServices] ILogger</c> parameter (the handler is already near the parameter-count
    /// threshold — see the B3/C1 extraction note). Both patterns are AOT-safe via source-gen.
    /// </remarks>
    public void LogSuggestionPersisted(
        string suggestionId,
        string conversationId,
        string leafNodeId,
        double confidence,
        string mode) =>
        LogSuggestionPersistedCore(_logger, suggestionId, conversationId, leafNodeId, confidence, mode);

    // ─── Structured log events (EventIds 6310+) ─────────────────────────────────

    [LoggerMessage(EventId = 6310, Level = LogLevel.Debug,
        Message = "AI typification suggestion persisted (id={SuggestionId}, conversation={ConversationId}, leaf={LeafNodeId}, confidence={Confidence:F3}, mode={Mode}).")]
    private static partial void LogSuggestionPersistedCore(
        ILogger logger,
        string suggestionId,
        string conversationId,
        string leafNodeId,
        double confidence,
        string mode);

    public void Dispose() => _meter.Dispose();
}
