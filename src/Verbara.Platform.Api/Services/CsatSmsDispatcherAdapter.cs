using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Channels.Sms;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.CsatRunner.Contracts;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform's in-process implementation of the Pro-defined
/// <see cref="ICsatSmsDispatcher"/> seam (csat-runner Phase E2, task 5b.3; Platform/ADR-0020 +
/// verbara-meta/ADR-0005 open-core boundary). Pro's SMS channel adapter calls
/// <see cref="SendAsync"/> via DI — same process, NOT an API call — to (1) send the locale-templated
/// outbound CSAT prompt via Platform's <see cref="ISmsProvider"/> and (2) record the Platform-owned
/// <c>csat_pending_dispatches</c> row keyed on <c>(recipientPhone, tenantId)</c> that the Phase-D
/// <c>CsatSmsCorrelator</c> consumes when the customer replies with a 1..5 rating.
/// </summary>
/// <remarks>
/// <para>
/// The written row matches the correlator's SELECT shape exactly:
/// <c>channel='sms'</c>, <c>correlator</c> = the recipient phone, <c>consumed_at</c> NULL,
/// <c>sent_at</c> = now, plus the <c>survey_id</c> / <c>queue_name</c> / <c>conversation_id</c> the
/// correlator forwards to the internal capture endpoint. The Pro <see cref="CsatSmsRequest"/> seam
/// carries only <c>ConversationId</c> (not the survey/queue), so this adapter resolves
/// <c>survey_id</c> and <c>queue_name</c> from the conversation → queue → active CSAT survey the same
/// way <see cref="CsatConversationEndSource"/> does; a missing CSAT survey degrades to a synthesized
/// non-empty <c>survey_id</c> so the NOT-NULL column is always satisfiable.
/// </para>
/// <para>
/// Idempotency (the seam's contract) is realized by a deterministic <c>dispatch_id</c> derived from
/// <c>(tenantId, recipientPhone, conversationId)</c> under an <c>ON CONFLICT DO NOTHING</c> upsert on
/// the <c>(tenant_id, dispatch_id)</c> primary key, so a retried dispatch records at most one open
/// row per <c>(recipientPhone, tenantId, conversationId)</c>. All data access uses the
/// <see cref="Verbara.Sdk.Data.Npgsql"/> facade with explicit <see cref="NpgsqlDbType"/> on nullable
/// params — Dapper is banned (Platform/ADR-0022). <see cref="NpgsqlDataSource"/> and
/// <see cref="ISmsProvider"/> are resolved optionally (both are absent under the Testing / in-memory
/// storage profile), so the adapter constructs for DI resolution and fails only at dispatch time when
/// SMS is not actually configured.
/// </para>
/// </remarks>
internal sealed class CsatSmsDispatcherAdapter : ICsatSmsDispatcher
{
    private readonly ISmsProvider? _smsProvider;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string _defaultFromNumber;
    private readonly IConversationStore _conversationStore;
    private readonly IQueueStore _queueStore;
    private readonly ISurveyStore _surveyStore;
    private readonly TimeProvider _timeProvider;

    public CsatSmsDispatcherAdapter(
        IConversationStore conversationStore,
        IQueueStore queueStore,
        ISurveyStore surveyStore,
        string defaultFromNumber,
        ISmsProvider? smsProvider = null,
        NpgsqlDataSource? dataSource = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(queueStore);
        ArgumentNullException.ThrowIfNull(surveyStore);
        _conversationStore = conversationStore;
        _queueStore = queueStore;
        _surveyStore = surveyStore;
        _defaultFromNumber = defaultFromNumber;
        _smsProvider = smsProvider;
        _dataSource = dataSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask SendAsync(CsatSmsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_smsProvider is null)
            throw new InvalidOperationException(
                "CSAT SMS dispatch requires a configured ISmsProvider (Twilio) — none is registered.");
        if (_dataSource is null)
            throw new InvalidOperationException(
                "CSAT SMS dispatch requires a Postgres NpgsqlDataSource to record csat_pending_dispatches — none is registered.");

        var tenant = new TenantId(request.TenantId);

        // Send the outbound prompt first — do not record a pending dispatch for a send that failed.
        var result = await _smsProvider
            .SendAsync(_defaultFromNumber, request.RecipientPhone, request.Body, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"CSAT SMS send failed for tenant '{request.TenantId}': {result.ErrorCode} {result.ErrorMessage}");

        // Resolve the survey/queue the correlator forwards — the seam carries only ConversationId.
        var (surveyId, queueName) = await ResolveSurveyAndQueueAsync(tenant, request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now + request.CorrelationWindow;
        // Deterministic id => idempotent re-dispatch within the window (ON CONFLICT DO NOTHING).
        var dispatchId = $"sms:{request.RecipientPhone}:{request.ConversationId}";

        await _dataSource.ExecuteAsync(
            "INSERT INTO csat_pending_dispatches " +
            "(dispatch_id, tenant_id, channel, correlator, survey_id, queue_name, conversation_id, sent_at, expires_at, consumed_at) " +
            "VALUES (@DispatchId, @TenantId, 'sms', @Correlator, @SurveyId, @QueueName, @ConversationId, @SentAt, @ExpiresAt, NULL) " +
            "ON CONFLICT (tenant_id, dispatch_id) DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("DispatchId", dispatchId));
                p.Add(new NpgsqlParameter("TenantId", request.TenantId));
                p.Add(new NpgsqlParameter("Correlator", request.RecipientPhone));
                p.Add(new NpgsqlParameter("SurveyId", surveyId));
                p.Add(new NpgsqlParameter("QueueName", NpgsqlDbType.Text) { Value = (object?)queueName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = (object?)request.ConversationId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SentAt", NpgsqlDbType.TimestampTz) { Value = now });
                p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = expiresAt });
            },
            cancellationToken).ConfigureAwait(false);
    }

    // survey_id is NOT NULL in csat_pending_dispatches; queue_name is nullable. The conversation's
    // queue owner yields the queue name; the tenant's active CSAT survey yields the survey id. When
    // no CSAT survey exists we synthesize a stable non-empty id rather than fail the NOT-NULL column.
    private async Task<(string SurveyId, string? QueueName)> ResolveSurveyAndQueueAsync(
        TenantId tenant, string conversationId, CancellationToken ct)
    {
        string? queueName = null;
        if (EntityId.IsValid(conversationId))
        {
            var conversation = await _conversationStore
                .GetByIdAsync(tenant, EntityId.From(conversationId), ct).ConfigureAwait(false);
            if (conversation?.Owner is { Kind: ConversationOwnerKind.Queue, OwnerId: { } queueId })
            {
                var queue = await _queueStore.GetByIdAsync(tenant, queueId, ct).ConfigureAwait(false);
                queueName = queue?.Name;
            }
        }

        var surveys = await _surveyStore.GetActiveAsync(tenant, ct).ConfigureAwait(false);
        var csatSurvey = surveys.FirstOrDefault(s => s.Type == SurveyType.Csat);
        var surveyId = csatSurvey?.SurveyId.Value ?? $"csat-sms:{tenant.Value}";

        return (surveyId, queueName);
    }
}
