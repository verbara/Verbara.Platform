using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// <see cref="IDialerDispositionDispatchStore"/> on <c>dialer_disposition_dispatches</c> (migration 025; design D16).
/// The claim is the outbound webhook claim's shape (Platform/ADR-0043): one autocommit statement whose CTE locks the
/// due rows with <c>FOR UPDATE SKIP LOCKED</c> and whose UPDATE writes a fresh claim and one more attempt, so two
/// dispatchers never claim one row; the completion and the release are fenced on the claim id.
/// </summary>
internal sealed class PostgresDialerDispositionDispatchStore : IDialerDispositionDispatchStore
{
    private const string Columns =
        "tenant_id, conversation_id, campaign_id, action_id, dialer_contact_id, disposition_code_id, disposition_code, " +
        "comment, callback_at, requests_callback, agent_id, attempts, next_attempt_at, claim_id, claim_expires_at, " +
        "result, completed_at, published_at, last_error, created_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresDialerDispositionDispatchStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Inserts <paramref name="dispatch"/> as given (pre-claimed by the request) inside the submission's transaction
    /// (<see cref="PostgresTypificationSubmissionStore.TryInsertAsync"/>).
    /// </summary>
    internal static Task<int> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DialerDispositionDispatch dispatch, CancellationToken ct) =>
        connection.ExecuteAsync(
            "INSERT INTO dialer_disposition_dispatches " +
            "(tenant_id, conversation_id, campaign_id, action_id, dialer_contact_id, disposition_code_id, " +
            " disposition_code, comment, callback_at, requests_callback, agent_id, attempts, next_attempt_at, " +
            " claim_id, claim_expires_at, created_at) " +
            "VALUES (@TenantId, @ConversationId, @CampaignId, @ActionId, @DialerContactId, @DispositionCodeId, " +
            " @DispositionCode, @Comment, @CallbackAt, @RequestsCallback, @AgentId, @Attempts, @NextAttemptAt, " +
            " @ClaimId, @ClaimExpiresAt, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = dispatch.TenantId });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = dispatch.ConversationId });
                p.Add(new NpgsqlParameter("CampaignId", NpgsqlDbType.Bigint) { Value = dispatch.CampaignId });
                p.Add(new NpgsqlParameter("ActionId", NpgsqlDbType.Text) { Value = (object?)dispatch.ActionId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DialerContactId", NpgsqlDbType.Bigint) { Value = (object?)dispatch.DialerContactId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DispositionCodeId", NpgsqlDbType.Bigint) { Value = dispatch.DispositionCodeId });
                p.Add(new NpgsqlParameter("DispositionCode", NpgsqlDbType.Text) { Value = (object?)dispatch.DispositionCode ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Comment", NpgsqlDbType.Text) { Value = (object?)dispatch.Comment ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CallbackAt", NpgsqlDbType.TimestampTz) { Value = (object?)dispatch.CallbackAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("RequestsCallback", NpgsqlDbType.Boolean) { Value = dispatch.RequestsCallback });
                p.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = (object?)dispatch.AgentId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Attempts", NpgsqlDbType.Integer) { Value = dispatch.Attempts });
                p.Add(new NpgsqlParameter("NextAttemptAt", NpgsqlDbType.TimestampTz) { Value = dispatch.NextAttemptAt.UtcDateTime });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Varchar) { Value = (object?)dispatch.ClaimId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ClaimExpiresAt", NpgsqlDbType.TimestampTz) { Value = (object?)dispatch.ClaimExpiresAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = dispatch.CreatedAt.UtcDateTime });
            },
            transaction,
            ct);

    public async Task<IReadOnlyList<DialerDispositionDispatch>> ClaimDueAsync(
        int limit, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // Each claimed row gets its own fencing id (a random 32-hex value, never a credential).
        var rows = await _dataSource.QueryListAsync(
            "WITH c AS (" +
            "SELECT tenant_id, conversation_id FROM dialer_disposition_dispatches " +
            "WHERE completed_at IS NULL AND next_attempt_at <= @Now " +
            "AND (claim_expires_at IS NULL OR claim_expires_at <= @Now) " +
            "ORDER BY next_attempt_at LIMIT @Limit FOR UPDATE SKIP LOCKED) " +
            "UPDATE dialer_disposition_dispatches d SET " +
            "claim_id = replace(gen_random_uuid()::text, '-', ''), claim_expires_at = @ExpiresAt, attempts = d.attempts + 1 " +
            "FROM c WHERE d.tenant_id = c.tenant_id AND d.conversation_id = c.conversation_id " +
            "RETURNING " + string.Join(", ", Columns.Split(", ").Select(c => "d." + c)),
            p =>
            {
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = expiresAt.UtcDateTime });
                p.Add(new NpgsqlParameter("Limit", NpgsqlDbType.Integer) { Value = limit });
            },
            DispatchRow.Map,
            ct);
        return rows;
    }

    public async Task<bool> CompleteAsync(
        DialerDispositionDispatch dispatch, string result, bool published, string claimId, CancellationToken ct)
    {
        var updated = await _dataSource.ExecuteAsync(
            "UPDATE dialer_disposition_dispatches SET result = @Result, completed_at = NOW(), " +
            "published_at = CASE WHEN @Published THEN NOW() ELSE NULL END, claim_id = NULL, claim_expires_at = NULL " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND claim_id = @ClaimId",
            p =>
            {
                p.Add(new NpgsqlParameter("Result", NpgsqlDbType.Text) { Value = result });
                p.Add(new NpgsqlParameter("Published", NpgsqlDbType.Boolean) { Value = published });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = dispatch.TenantId });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = dispatch.ConversationId });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Varchar) { Value = claimId });
            },
            ct);
        return updated == 1;
    }

    public async Task<bool> ReleaseAsync(
        DialerDispositionDispatch dispatch, string lastError, DateTimeOffset nextAttemptAt, string claimId, CancellationToken ct)
    {
        var updated = await _dataSource.ExecuteAsync(
            "UPDATE dialer_disposition_dispatches SET last_error = @Error, next_attempt_at = @NextAttemptAt, " +
            "claim_id = NULL, claim_expires_at = NULL " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND claim_id = @ClaimId",
            p =>
            {
                p.Add(new NpgsqlParameter("Error", NpgsqlDbType.Text) { Value = lastError });
                p.Add(new NpgsqlParameter("NextAttemptAt", NpgsqlDbType.TimestampTz) { Value = nextAttemptAt.UtcDateTime });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = dispatch.TenantId });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = dispatch.ConversationId });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Varchar) { Value = claimId });
            },
            ct);
        return updated == 1;
    }

    public async Task<long> CountPendingAsync(CancellationToken ct) =>
        await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM dialer_disposition_dispatches WHERE completed_at IS NULL",
            _ => { },
            ct) ?? 0L;

    /// <summary>The row of one Conversation, as stored (tests and diagnostics).</summary>
    internal Task<DialerDispositionDispatch?> GetAsync(string tenantId, string conversationId, CancellationToken ct) =>
        _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {Columns} FROM dialer_disposition_dispatches " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId });
            },
            DispatchRow.Map,
            ct);

    private static class DispatchRow
    {
        public static DialerDispositionDispatch Map(NpgsqlDataReader r) => new()
        {
            TenantId = r.GetString("tenant_id"),
            ConversationId = r.GetString("conversation_id"),
            CampaignId = r.GetInt64("campaign_id"),
            ActionId = r.GetString("action_id"),
            DialerContactId = r.GetInt64OrNull("dialer_contact_id"),
            DispositionCodeId = r.GetInt64("disposition_code_id"),
            DispositionCode = r.GetString("disposition_code"),
            Comment = r.GetStringOrNull("comment"),
            CallbackAt = Utc(r.GetDateTimeOrNull("callback_at")),
            RequestsCallback = r.GetBoolean("requests_callback"),
            AgentId = r.GetStringOrNull("agent_id"),
            Attempts = r.GetInt32("attempts"),
            NextAttemptAt = new DateTimeOffset(r.GetDateTime("next_attempt_at"), TimeSpan.Zero),
            ClaimId = r.GetStringOrNull("claim_id"),
            ClaimExpiresAt = Utc(r.GetDateTimeOrNull("claim_expires_at")),
            Result = r.GetStringOrNull("result"),
            CompletedAt = Utc(r.GetDateTimeOrNull("completed_at")),
            PublishedAt = Utc(r.GetDateTimeOrNull("published_at")),
            LastError = r.GetStringOrNull("last_error"),
            CreatedAt = new DateTimeOffset(r.GetDateTime("created_at"), TimeSpan.Zero),
        };

        private static DateTimeOffset? Utc(DateTime? value) =>
            value is { } v ? new DateTimeOffset(v, TimeSpan.Zero) : null;
    }
}
