using System.Text.Json;
using Dapper;
using Npgsql;
using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWebhookSubscriptionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "subscription_id, tenant_id, name, endpoint_url, secret, event_types, " +
        "is_active, created_at, updated_at, " +
        "circuit_status, circuit_failures, circuit_opened_at, circuit_next_probe_at, circuit_probe_attempts";

    public async Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SubscriptionRow>(
            $"SELECT {SelectColumns} FROM webhook_subscriptions WHERE subscription_id = @Id",
            new { Id = subscriptionId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SubscriptionRow>(
            $"SELECT {SelectColumns} FROM webhook_subscriptions WHERE tenant_id = @TenantId ORDER BY created_at DESC",
            new { TenantId = tenantId });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SubscriptionRow>(
            $"SELECT {SelectColumns} FROM webhook_subscriptions " +
            "WHERE tenant_id = @TenantId AND is_active = true AND event_types @> @EventTypeJson::jsonb",
            new { TenantId = tenantId, EventTypeJson = $"[\"{eventType}\"]" });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        var eventTypesJson = JsonSerializer.Serialize(subscription.EventTypes, PostgresJson.Ctx.IReadOnlyListString);
        var circuitStatus = subscription.CircuitStatus.ToString().ToLowerInvariant();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO webhook_subscriptions (subscription_id, tenant_id, name, endpoint_url, " +
            "secret, event_types, is_active, created_at, updated_at, " +
            "circuit_status, circuit_failures, circuit_opened_at, circuit_next_probe_at, circuit_probe_attempts) " +
            "VALUES (@SubscriptionId, @TenantId, @Name, @EndpointUrl, @Secret, " +
            "@EventTypes::jsonb, @IsActive, @CreatedAt, @UpdatedAt, " +
            "@CircuitStatus, @CircuitFailures, @CircuitOpenedAt, @CircuitNextProbeAt, @CircuitProbeAttempts) " +
            "ON CONFLICT (subscription_id) DO UPDATE SET " +
            "name = EXCLUDED.name, endpoint_url = EXCLUDED.endpoint_url, " +
            "secret = EXCLUDED.secret, event_types = EXCLUDED.event_types, " +
            "is_active = EXCLUDED.is_active, updated_at = EXCLUDED.updated_at, " +
            "circuit_status = EXCLUDED.circuit_status, circuit_failures = EXCLUDED.circuit_failures, " +
            "circuit_opened_at = EXCLUDED.circuit_opened_at, " +
            "circuit_next_probe_at = EXCLUDED.circuit_next_probe_at, " +
            "circuit_probe_attempts = EXCLUDED.circuit_probe_attempts",
            new
            {
                subscription.SubscriptionId,
                subscription.TenantId,
                subscription.Name,
                subscription.EndpointUrl,
                subscription.Secret,
                EventTypes = eventTypesJson,
                subscription.IsActive,
                subscription.CreatedAt,
                subscription.UpdatedAt,
                CircuitStatus = circuitStatus,
                subscription.CircuitFailures,
                subscription.CircuitOpenedAt,
                subscription.CircuitNextProbeAt,
                subscription.CircuitProbeAttempts,
            });
    }

    public async Task DeleteAsync(string subscriptionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM webhook_subscriptions WHERE subscription_id = @Id",
            new { Id = subscriptionId });
    }

    private sealed class SubscriptionRow
    {
        public string subscription_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string endpoint_url { get; init; } = null!;
        public string secret { get; init; } = null!;
        public string event_types { get; init; } = null!;
        public bool is_active { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }
        public string circuit_status { get; init; } = "closed";
        public int circuit_failures { get; init; }
        public DateTime? circuit_opened_at { get; init; }
        public DateTime? circuit_next_probe_at { get; init; }
        public int circuit_probe_attempts { get; init; }

        public WebhookSubscription ToModel()
        {
            var types = JsonSerializer.Deserialize(event_types, PostgresJson.Ctx.IReadOnlyListString)
                ?? (IReadOnlyList<string>)[];
            var circuitStatus = Enum.TryParse<CircuitStatus>(circuit_status, ignoreCase: true, out var cs)
                ? cs
                : CircuitStatus.Closed;
            return new WebhookSubscription(
                subscription_id, tenant_id, name, endpoint_url, secret,
                types, is_active, created_at, updated_at,
                circuitStatus, circuit_failures,
                circuit_opened_at.HasValue ? new DateTimeOffset(circuit_opened_at.Value, TimeSpan.Zero) : null,
                circuit_next_probe_at.HasValue ? new DateTimeOffset(circuit_next_probe_at.Value, TimeSpan.Zero) : null,
                circuit_probe_attempts);
        }
    }
}
