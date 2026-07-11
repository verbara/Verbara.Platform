using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// Resolves a CSAT email dispatch out of <c>csat_pending_dispatches</c> (csat-runner Phase C). The
/// email channel stores the reply token in the <c>correlator</c> column and the dispatched
/// <c>Message-Id</c> in <c>conversation_id</c>'s sibling columns — resolution hydrates the
/// <c>surveyId</c>/<c>queueName</c>/<c>conversationId</c> the internal email endpoint needs but the
/// reply token does not carry. All access uses the Npgsql facade (no Dapper, ADR-0022).
/// </summary>
internal sealed class PostgresCsatEmailDispatchResolver : ICsatEmailDispatchResolver
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCsatEmailDispatchResolver(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "SELECT tenant_id, survey_id, queue_name, conversation_id ";

    public async Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct)
    {
        return await _dataSource.QueryFirstOrDefaultAsync(
            SelectColumns +
            "FROM csat_pending_dispatches " +
            "WHERE channel = 'email' AND correlator = @Correlator AND consumed_at IS NULL " +
            "ORDER BY sent_at DESC",
            p => p.Add(new NpgsqlParameter("Correlator", token)),
            Map,
            ct).ConfigureAwait(false);
    }

    public async Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct)
    {
        // The forwarder-stripped path correlates on the dispatched Message-Id, persisted in the
        // dispatch row's dispatch_id (Pro stamps Message-Id = the dispatch id at send time).
        return await _dataSource.QueryFirstOrDefaultAsync(
            SelectColumns +
            "FROM csat_pending_dispatches " +
            "WHERE channel = 'email' AND dispatch_id = @DispatchId AND consumed_at IS NULL " +
            "ORDER BY sent_at DESC",
            p => p.Add(new NpgsqlParameter("DispatchId", inReplyToMessageId)),
            Map,
            ct).ConfigureAwait(false);
    }

    private static CsatEmailDispatch Map(NpgsqlDataReader r) => new(
        TenantId: r.GetString("tenant_id"),
        SurveyId: r.GetString("survey_id"),
        QueueName: r.GetStringOrNull("queue_name") ?? string.Empty,
        ConversationId: r.GetStringOrNull("conversation_id") ?? string.Empty);
}
