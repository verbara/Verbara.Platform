using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Channels.Sms;

/// <summary>
/// Correlates an inbound SMS against an open CSAT dispatch (csat-runner Phase D). Plugs into the
/// inbound SMS path AFTER <see cref="SmsWebhookHandler"/> has parsed a new message: for an inbound
/// <c>(tenantId, fromNumber)</c> it looks up <c>csat_pending_dispatches</c> for an unconsumed row
/// inside the 24h window and, when the body is a bare <c>1..5</c> digit, forwards the rating to the
/// internal capture endpoint and marks the dispatch consumed. When no dispatch matches OR the body
/// is not a bare digit, it falls through so the SMS is routed as a normal conversation message and
/// no user message is consumed (spec: "Internal SMS CSAT capture with correlation fall-through").
///
/// Collision rule: when two dispatches to the same phone are open within 24h, the reply is
/// attributed to the most-recent dispatch and any older open dispatch is marked expired
/// (<c>consumed_at</c> stamped) without a capture.
///
/// All data access uses the <see cref="Verbara.Sdk.Data.Npgsql"/> facade with explicit
/// <see cref="NpgsqlDbType"/> on nullable params — Dapper is banned (Platform/ADR-0022).
/// </summary>
public sealed partial class CsatSmsCorrelator
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ICsatCaptureForwarder _forwarder;
    private readonly TimeProvider _timeProvider;

    public CsatSmsCorrelator(
        NpgsqlDataSource dataSource,
        ICsatCaptureForwarder forwarder,
        TimeProvider? timeProvider = null)
    {
        _dataSource = dataSource;
        _forwarder = forwarder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Bare single-digit 1..5 (whole body), tolerating surrounding whitespace only — spec regex.
    [GeneratedRegex(@"^\s*[1-5]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RatingRegex();

    /// <summary>
    /// Attempts to correlate an inbound SMS body against an open CSAT dispatch for
    /// <paramref name="fromNumber"/>. Returns <see langword="true"/> when the message was captured
    /// as a CSAT rating (the caller MUST then stop normal routing so the user message is not also
    /// delivered as a conversation message); returns <see langword="false"/> to fall through to
    /// normal conversation routing.
    /// </summary>
    public async Task<bool> TryCorrelateAsync(
        TenantId tenantId,
        string fromNumber,
        string body,
        CancellationToken ct)
    {
        // Load open, in-window dispatches most-recent-first. The window/consumed filter mirrors the
        // frozen spec SQL; ORDER BY sent_at DESC realizes the "most-recent wins" collision rule.
        var now = _timeProvider.GetUtcNow();
        var windowStart = now - TimeSpan.FromHours(24);

        var open = await _dataSource.QueryListAsync(
            "SELECT dispatch_id, tenant_id, correlator, survey_id, queue_name, conversation_id, sent_at " +
            "FROM csat_pending_dispatches " +
            "WHERE tenant_id = @TenantId AND channel = 'sms' AND correlator = @Correlator " +
            "AND consumed_at IS NULL AND sent_at > @WindowStart " +
            "ORDER BY sent_at DESC",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Correlator", fromNumber));
                p.Add(new NpgsqlParameter("WindowStart", NpgsqlDbType.TimestampTz) { Value = windowStart });
            },
            PendingDispatchRow.Map,
            ct).ConfigureAwait(false);

        // No open dispatch → not a CSAT reply; route normally.
        if (open.Count == 0)
            return false;

        // Not a bare rating digit → do NOT consume any dispatch; fall through to routing.
        var match = RatingRegex().Match(body);
        if (!match.Success)
            return false;

        var rating = int.Parse(match.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var winner = open[0];

        // Collision: mark every older open dispatch expired (consumed, no capture) — most-recent wins.
        for (var i = 1; i < open.Count; i++)
            await MarkConsumedAsync(tenantId, open[i].DispatchId, now, ct).ConfigureAwait(false);

        // Forward the rating to the internal capture endpoint, then consume the winning dispatch.
        await _forwarder.ForwardSmsRatingAsync(
            tenantId,
            winner.SurveyId,
            winner.QueueName ?? string.Empty,
            winner.ConversationId ?? string.Empty,
            rating,
            now,
            ct).ConfigureAwait(false);

        await MarkConsumedAsync(tenantId, winner.DispatchId, now, ct).ConfigureAwait(false);
        return true;
    }

    private async Task MarkConsumedAsync(TenantId tenantId, string dispatchId, DateTimeOffset consumedAt, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE csat_pending_dispatches SET consumed_at = @ConsumedAt " +
            "WHERE tenant_id = @TenantId AND dispatch_id = @DispatchId AND consumed_at IS NULL",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("DispatchId", dispatchId));
                p.Add(new NpgsqlParameter("ConsumedAt", NpgsqlDbType.TimestampTz) { Value = consumedAt });
            },
            ct).ConfigureAwait(false);
    }

    private sealed class PendingDispatchRow
    {
        public string DispatchId { get; init; } = null!;
        public string TenantId { get; init; } = null!;
        public string Correlator { get; init; } = null!;
        public string SurveyId { get; init; } = null!;
        public string? QueueName { get; init; }
        public string? ConversationId { get; init; }
        public DateTimeOffset SentAt { get; init; }

        public static PendingDispatchRow Map(NpgsqlDataReader r) => new()
        {
            DispatchId = r.GetString("dispatch_id"),
            TenantId = r.GetString("tenant_id"),
            Correlator = r.GetString("correlator"),
            SurveyId = r.GetString("survey_id"),
            QueueName = r.GetStringOrNull("queue_name"),
            ConversationId = r.GetStringOrNull("conversation_id"),
            SentAt = r.GetDateTimeOffset("sent_at"),
        };
    }
}
