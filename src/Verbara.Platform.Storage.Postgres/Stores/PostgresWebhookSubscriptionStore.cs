using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Data.Npgsql;

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
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM webhook_subscriptions WHERE subscription_id = @Id",
            p => p.Add(new NpgsqlParameter("Id", subscriptionId)),
            SubscriptionRow.Map, ct);
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM webhook_subscriptions WHERE tenant_id = @TenantId ORDER BY created_at DESC",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            SubscriptionRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM webhook_subscriptions " +
            "WHERE tenant_id = @TenantId AND is_active = true AND event_types @> @EventTypeJson::jsonb",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("EventTypeJson", $"[\"{eventType}\"]"));
            },
            SubscriptionRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        var eventTypesJson = JsonSerializer.Serialize(subscription.EventTypes, PostgresJson.Ctx.IReadOnlyListString);
        var circuitStatus = subscription.CircuitStatus.ToString().ToLowerInvariant();

        await _dataSource.ExecuteAsync(
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
            p =>
            {
                p.Add(new NpgsqlParameter("SubscriptionId", subscription.SubscriptionId));
                p.Add(new NpgsqlParameter("TenantId", subscription.TenantId));
                p.Add(new NpgsqlParameter("Name", subscription.Name));
                p.Add(new NpgsqlParameter("EndpointUrl", subscription.EndpointUrl));
                p.Add(new NpgsqlParameter("Secret", subscription.Secret));
                p.Add(new NpgsqlParameter("EventTypes", eventTypesJson));
                p.Add(new NpgsqlParameter("IsActive", subscription.IsActive));
                p.Add(new NpgsqlParameter("CreatedAt", subscription.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", subscription.UpdatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("CircuitStatus", circuitStatus));
                p.Add(new NpgsqlParameter("CircuitFailures", subscription.CircuitFailures));
                p.Add(new NpgsqlParameter("CircuitOpenedAt", (object?)subscription.CircuitOpenedAt?.UtcDateTime ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CircuitNextProbeAt", (object?)subscription.CircuitNextProbeAt?.UtcDateTime ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CircuitProbeAttempts", subscription.CircuitProbeAttempts));
            },
            ct);
    }

    public async Task DeleteAsync(string subscriptionId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM webhook_subscriptions WHERE subscription_id = @Id",
            p => p.Add(new NpgsqlParameter("Id", subscriptionId)),
            ct);
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

        public static SubscriptionRow Map(NpgsqlDataReader r) => new()
        {
            subscription_id = r.GetString("subscription_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            endpoint_url = r.GetString("endpoint_url"),
            secret = r.GetString("secret"),
            event_types = r.GetString("event_types"),
            is_active = r.GetBoolean("is_active"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTime("updated_at"),
            circuit_status = r.GetString("circuit_status"),
            circuit_failures = r.GetInt32("circuit_failures"),
            circuit_opened_at = r.GetDateTimeOrNull("circuit_opened_at"),
            circuit_next_probe_at = r.GetDateTimeOrNull("circuit_next_probe_at"),
            circuit_probe_attempts = r.GetInt32("circuit_probe_attempts"),
        };

        public WebhookSubscription ToModel()
        {
            var types = JsonSerializer.Deserialize(event_types, PostgresJson.Ctx.IReadOnlyListString)
                ?? (IReadOnlyList<string>)[];
            var circuitStatus = Enum.TryParse<CircuitStatus>(circuit_status, ignoreCase: true, out var cs)
                ? cs
                : CircuitStatus.Closed;
            return new WebhookSubscription(
                subscription_id, tenant_id, name, endpoint_url, secret,
                types, is_active,
                new DateTimeOffset(created_at, TimeSpan.Zero),
                new DateTimeOffset(updated_at, TimeSpan.Zero),
                circuitStatus, circuit_failures,
                circuit_opened_at.HasValue ? new DateTimeOffset(circuit_opened_at.Value, TimeSpan.Zero) : null,
                circuit_next_probe_at.HasValue ? new DateTimeOffset(circuit_next_probe_at.Value, TimeSpan.Zero) : null,
                circuit_probe_attempts);
        }
    }
}
