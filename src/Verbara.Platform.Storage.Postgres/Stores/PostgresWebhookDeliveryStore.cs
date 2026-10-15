using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWebhookDeliveryStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO webhook_deliveries (delivery_id, tenant_id, subscription_id, event_type, " +
            "payload, status, attempts, max_attempts, next_retry_at, last_response_code, " +
            "last_error, created_at, delivered_at) " +
            "VALUES (@DeliveryId, @TenantId, @SubscriptionId, @EventType, " +
            "@Payload::jsonb, @Status, @Attempts, @MaxAttempts, @NextRetryAt, @LastResponseCode, " +
            "@LastError, @CreatedAt, @DeliveredAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("DeliveryId", delivery.DeliveryId));
                p.Add(new NpgsqlParameter("TenantId", delivery.TenantId));
                p.Add(new NpgsqlParameter("SubscriptionId", delivery.SubscriptionId));
                p.Add(new NpgsqlParameter("EventType", delivery.EventType));
                p.Add(new NpgsqlParameter("Payload", delivery.Payload));
                p.Add(new NpgsqlParameter("Status", delivery.Status.ToString()));
                p.Add(new NpgsqlParameter("Attempts", delivery.Attempts));
                p.Add(new NpgsqlParameter("MaxAttempts", delivery.MaxAttempts));
                p.Add(new NpgsqlParameter("NextRetryAt", NpgsqlDbType.TimestampTz) { Value = (object?)delivery.NextRetryAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastResponseCode", NpgsqlDbType.Integer) { Value = (object?)delivery.LastResponseCode ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastError", NpgsqlDbType.Text) { Value = (object?)delivery.LastError ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", delivery.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("DeliveredAt", NpgsqlDbType.TimestampTz) { Value = (object?)delivery.DeliveredAt?.UtcDateTime ?? DBNull.Value });
            },
            ct);
    }

    public async Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries WHERE delivery_id = @Id",
            p => p.Add(new NpgsqlParameter("Id", deliveryId)),
            DeliveryRow.Map, ct);
        return row?.ToModel();
    }

    public async Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM webhook_deliveries WHERE subscription_id = @SubId",
            p => p.Add(new NpgsqlParameter("SubId", subscriptionId)),
            ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries WHERE subscription_id = @SubId " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                p.Add(new NpgsqlParameter("SubId", subscriptionId));
                p.Add(new NpgsqlParameter("Limit", pageSize));
                p.Add(new NpgsqlParameter("Offset", (page - 1) * pageSize));
            },
            DeliveryRow.Map, ct);

        return new PagedResult<WebhookDelivery>(rows.Select(r => r.ToModel()).ToList(), total, page, pageSize);
    }

    public async Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM webhook_deliveries WHERE tenant_id = @TenantId AND status = 'DeadLetter'",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries WHERE tenant_id = @TenantId AND status = 'DeadLetter' " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Limit", pageSize));
                p.Add(new NpgsqlParameter("Offset", (page - 1) * pageSize));
            },
            DeliveryRow.Map, ct);

        return new PagedResult<WebhookDelivery>(rows.Select(r => r.ToModel()).ToList(), total, page, pageSize);
    }

    public async Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE webhook_deliveries SET " +
            "status = @Status, attempts = @Attempts, next_retry_at = @NextRetryAt, " +
            "last_response_code = @LastResponseCode, last_error = @LastError, " +
            "delivered_at = @DeliveredAt " +
            "WHERE delivery_id = @DeliveryId",
            p =>
            {
                p.Add(new NpgsqlParameter("Status", delivery.Status.ToString()));
                p.Add(new NpgsqlParameter("Attempts", delivery.Attempts));
                p.Add(new NpgsqlParameter("NextRetryAt", NpgsqlDbType.TimestampTz) { Value = (object?)delivery.NextRetryAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastResponseCode", NpgsqlDbType.Integer) { Value = (object?)delivery.LastResponseCode ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastError", NpgsqlDbType.Text) { Value = (object?)delivery.LastError ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DeliveredAt", NpgsqlDbType.TimestampTz) { Value = (object?)delivery.DeliveredAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DeliveryId", delivery.DeliveryId));
            },
            ct);
    }

    public async Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM webhook_deliveries WHERE subscription_id = @SubId",
            p => p.Add(new NpgsqlParameter("SubId", subscriptionId)),
            ct);
    }

    // ── Claims (webhook-delivery-runs-once, design D2, D5 and D8; Platform/ADR-0043) ──────────────────────
    //
    // A claim is the committed row change (claim_id, claim_expires_at), never a lock alone. Each claim is one
    // autocommit statement: the CTE locks the candidate row with FOR UPDATE SKIP LOCKED, and the UPDATE writes
    // the claim. Under READ COMMITTED, FOR UPDATE re-evaluates the predicate on the newest row version, so a row
    // another caller claimed a moment earlier no longer matches and is skipped; after the commit, the
    // claim_expires_at predicate keeps every other caller out until the lease passes. SKIP LOCKED means a row
    // being claimed at this instant is skipped, never waited for: the lock holder delivers it, or it stays due
    // for the next poll (design D2 trade-off). previous_claim_id is the claim this one took over, if any.

    private const string ClaimablePredicate =
        "status = 'Pending' AND next_retry_at IS NOT NULL AND next_retry_at <= @Now " +
        "AND (claim_expires_at IS NULL OR claim_expires_at <= @Now)";

    private const string ClaimUpdate =
        "UPDATE webhook_deliveries d SET claim_id = @ClaimId, claim_expires_at = @ExpiresAt " +
        "FROM c WHERE d.delivery_id = c.delivery_id " +
        "RETURNING d.delivery_id, d.tenant_id, d.subscription_id, d.event_type, d.payload, d.status, " +
        "d.attempts, d.max_attempts, d.next_retry_at, d.last_response_code, d.last_error, " +
        "d.created_at, d.delivered_at, c.previous_claim_id";

    private const string ClaimNextDueSql =
        "WITH c AS (" +
        "SELECT delivery_id, claim_id AS previous_claim_id FROM webhook_deliveries " +
        "WHERE " + ClaimablePredicate + " " +
        "ORDER BY next_retry_at LIMIT 1 FOR UPDATE SKIP LOCKED) " +
        ClaimUpdate;

    private const string ClaimByIdSql =
        "WITH c AS (" +
        "SELECT delivery_id, claim_id AS previous_claim_id FROM webhook_deliveries " +
        "WHERE delivery_id = @DeliveryId AND " + ClaimablePredicate + " " +
        "FOR UPDATE SKIP LOCKED) " +
        ClaimUpdate;

    public Task<WebhookDeliveryClaim?> TryClaimAsync(
        string deliveryId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct) =>
        ClaimAsync(ClaimByIdSql, deliveryId, now, expiresAt, ct);

    public Task<WebhookDeliveryClaim?> ClaimNextDueAsync(
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct) =>
        ClaimAsync(ClaimNextDueSql, deliveryId: null, now, expiresAt, ct);

    private async Task<WebhookDeliveryClaim?> ClaimAsync(
        string sql, string? deliveryId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // A fencing id, not a credential (design D8): minted here, never interpolated.
        var claimId = Guid.NewGuid().ToString("N");

        var row = await _dataSource.QueryFirstOrDefaultAsync(
            sql,
            p =>
            {
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Varchar) { Value = claimId });
                p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = expiresAt.UtcDateTime });
                if (deliveryId is not null)
                    p.Add(new NpgsqlParameter("DeliveryId", NpgsqlDbType.Varchar) { Value = deliveryId });
            },
            ClaimedDeliveryRow.Map, ct);

        return row is null
            ? null
            : new WebhookDeliveryClaim(row.Delivery.ToModel(), claimId, Reclaimed: row.previous_claim_id is not null);
    }

    public async Task<bool> CompleteAttemptAsync(WebhookDelivery outcome, string claimId, CancellationToken ct)
    {
        var updated = await _dataSource.ExecuteAsync(
            "UPDATE webhook_deliveries SET " +
            "status = @Status, attempts = @Attempts, next_retry_at = @NextRetryAt, " +
            "last_response_code = @LastResponseCode, last_error = @LastError, " +
            "delivered_at = @DeliveredAt, claim_id = NULL, claim_expires_at = NULL " +
            "WHERE delivery_id = @DeliveryId AND claim_id = @ClaimId",
            p =>
            {
                p.Add(new NpgsqlParameter("Status", NpgsqlDbType.Varchar) { Value = outcome.Status.ToString() });
                p.Add(new NpgsqlParameter("Attempts", NpgsqlDbType.Integer) { Value = outcome.Attempts });
                p.Add(new NpgsqlParameter("NextRetryAt", NpgsqlDbType.TimestampTz) { Value = (object?)outcome.NextRetryAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastResponseCode", NpgsqlDbType.Integer) { Value = (object?)outcome.LastResponseCode ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastError", NpgsqlDbType.Text) { Value = (object?)outcome.LastError ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DeliveredAt", NpgsqlDbType.TimestampTz) { Value = (object?)outcome.DeliveredAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DeliveryId", NpgsqlDbType.Varchar) { Value = outcome.DeliveryId });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Varchar) { Value = claimId });
            },
            ct);
        return updated == 1;
    }

    public async Task<bool> ReleaseClaimAsync(
        string deliveryId, string claimId, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        var updated = await _dataSource.ExecuteAsync(
            "UPDATE webhook_deliveries SET " +
            "next_retry_at = @NextRetryAt, claim_id = NULL, claim_expires_at = NULL " +
            "WHERE delivery_id = @DeliveryId AND claim_id = @ClaimId",
            p =>
            {
                p.Add(new NpgsqlParameter("NextRetryAt", NpgsqlDbType.TimestampTz) { Value = nextRetryAt.UtcDateTime });
                p.Add(new NpgsqlParameter("DeliveryId", NpgsqlDbType.Varchar) { Value = deliveryId });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Varchar) { Value = claimId });
            },
            ct);
        return updated == 1;
    }

    private sealed class ClaimedDeliveryRow
    {
        public DeliveryRow Delivery { get; init; } = null!;
        public string? previous_claim_id { get; init; }

        public static ClaimedDeliveryRow Map(NpgsqlDataReader r) => new()
        {
            Delivery = DeliveryRow.Map(r),
            previous_claim_id = r.GetStringOrNull("previous_claim_id"),
        };
    }

    private sealed class DeliveryRow
    {
        public string delivery_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string subscription_id { get; init; } = null!;
        public string event_type { get; init; } = null!;
        public string payload { get; init; } = null!;
        public string status { get; init; } = null!;
        public int attempts { get; init; }
        public int max_attempts { get; init; }
        public DateTime? next_retry_at { get; init; }
        public int? last_response_code { get; init; }
        public string? last_error { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? delivered_at { get; init; }

        public static DeliveryRow Map(NpgsqlDataReader r) => new()
        {
            delivery_id = r.GetString("delivery_id"),
            tenant_id = r.GetString("tenant_id"),
            subscription_id = r.GetString("subscription_id"),
            event_type = r.GetString("event_type"),
            payload = r.GetString("payload"),
            status = r.GetString("status"),
            attempts = r.GetInt32("attempts"),
            max_attempts = r.GetInt32("max_attempts"),
            next_retry_at = r.GetDateTimeOrNull("next_retry_at"),
            last_response_code = r.GetInt32OrNull("last_response_code"),
            last_error = r.GetStringOrNull("last_error"),
            created_at = r.GetDateTime("created_at"),
            delivered_at = r.GetDateTimeOrNull("delivered_at"),
        };

        public WebhookDelivery ToModel() => new(
            delivery_id, tenant_id, subscription_id, event_type, payload,
            Enum.Parse<WebhookDeliveryStatus>(status),
            attempts, max_attempts,
            next_retry_at.HasValue ? new DateTimeOffset(next_retry_at.Value, TimeSpan.Zero) : null,
            last_response_code,
            last_error,
            new DateTimeOffset(created_at, TimeSpan.Zero),
            delivered_at.HasValue ? new DateTimeOffset(delivered_at.Value, TimeSpan.Zero) : null);
    }
}
