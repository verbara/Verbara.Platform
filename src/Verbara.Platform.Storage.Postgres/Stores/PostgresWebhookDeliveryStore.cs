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

    public async Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries " +
            "WHERE status = 'Pending' AND next_retry_at IS NOT NULL AND next_retry_at <= @Now " +
            "ORDER BY next_retry_at LIMIT @BatchSize",
            p =>
            {
                p.Add(new NpgsqlParameter("Now", now.UtcDateTime));
                p.Add(new NpgsqlParameter("BatchSize", batchSize));
            },
            DeliveryRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
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
