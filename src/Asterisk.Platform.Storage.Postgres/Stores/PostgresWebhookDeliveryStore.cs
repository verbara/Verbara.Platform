using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWebhookDeliveryStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO webhook_deliveries (delivery_id, tenant_id, subscription_id, event_type, " +
            "payload, status, attempts, max_attempts, next_retry_at, last_response_code, " +
            "last_error, created_at, delivered_at) " +
            "VALUES (@DeliveryId, @TenantId, @SubscriptionId, @EventType, " +
            "@Payload::jsonb, @Status, @Attempts, @MaxAttempts, @NextRetryAt, @LastResponseCode, " +
            "@LastError, @CreatedAt, @DeliveredAt)",
            new
            {
                delivery.DeliveryId,
                delivery.TenantId,
                delivery.SubscriptionId,
                delivery.EventType,
                delivery.Payload,
                Status = delivery.Status.ToString(),
                delivery.Attempts,
                delivery.MaxAttempts,
                delivery.NextRetryAt,
                delivery.LastResponseCode,
                delivery.LastError,
                delivery.CreatedAt,
                delivery.DeliveredAt,
            });
    }

    public async Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DeliveryRow>(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries WHERE delivery_id = @Id",
            new { Id = deliveryId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DeliveryRow>(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries " +
            "WHERE status = 'Pending' AND next_retry_at IS NOT NULL AND next_retry_at <= @Now " +
            "ORDER BY next_retry_at LIMIT @BatchSize",
            new { Now = now, BatchSize = batchSize });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM webhook_deliveries WHERE subscription_id = @SubId",
            new { SubId = subscriptionId });

        var rows = await conn.QueryAsync<DeliveryRow>(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries WHERE subscription_id = @SubId " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            new { SubId = subscriptionId, Limit = pageSize, Offset = (page - 1) * pageSize });

        return new PagedResult<WebhookDelivery>(rows.Select(r => r.ToModel()).ToList(), total, page, pageSize);
    }

    public async Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM webhook_deliveries WHERE tenant_id = @TenantId AND status = 'DeadLetter'",
            new { TenantId = tenantId });

        var rows = await conn.QueryAsync<DeliveryRow>(
            "SELECT delivery_id, tenant_id, subscription_id, event_type, payload, status, " +
            "attempts, max_attempts, next_retry_at, last_response_code, last_error, " +
            "created_at, delivered_at " +
            "FROM webhook_deliveries WHERE tenant_id = @TenantId AND status = 'DeadLetter' " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId, Limit = pageSize, Offset = (page - 1) * pageSize });

        return new PagedResult<WebhookDelivery>(rows.Select(r => r.ToModel()).ToList(), total, page, pageSize);
    }

    public async Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE webhook_deliveries SET " +
            "status = @Status, attempts = @Attempts, next_retry_at = @NextRetryAt, " +
            "last_response_code = @LastResponseCode, last_error = @LastError, " +
            "delivered_at = @DeliveredAt " +
            "WHERE delivery_id = @DeliveryId",
            new
            {
                Status = delivery.Status.ToString(),
                delivery.Attempts,
                delivery.NextRetryAt,
                delivery.LastResponseCode,
                delivery.LastError,
                delivery.DeliveredAt,
                delivery.DeliveryId,
            });
    }

    public async Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM webhook_deliveries WHERE subscription_id = @SubId",
            new { SubId = subscriptionId });
    }

    private sealed record DeliveryRow(
        string delivery_id,
        string tenant_id,
        string subscription_id,
        string event_type,
        string payload,
        string status,
        int attempts,
        int max_attempts,
        DateTimeOffset? next_retry_at,
        int? last_response_code,
        string? last_error,
        DateTimeOffset created_at,
        DateTimeOffset? delivered_at)
    {
        public WebhookDelivery ToModel() => new(
            delivery_id, tenant_id, subscription_id, event_type, payload,
            Enum.Parse<WebhookDeliveryStatus>(status),
            attempts, max_attempts, next_retry_at, last_response_code,
            last_error, created_at, delivered_at);
    }
}
