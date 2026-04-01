using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWebhookSubscriptionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SubscriptionRow>(
            "SELECT subscription_id, tenant_id, name, endpoint_url, secret, event_types, " +
            "is_active, created_at, updated_at " +
            "FROM webhook_subscriptions WHERE subscription_id = @Id",
            new { Id = subscriptionId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SubscriptionRow>(
            "SELECT subscription_id, tenant_id, name, endpoint_url, secret, event_types, " +
            "is_active, created_at, updated_at " +
            "FROM webhook_subscriptions WHERE tenant_id = @TenantId ORDER BY created_at DESC",
            new { TenantId = tenantId });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SubscriptionRow>(
            "SELECT subscription_id, tenant_id, name, endpoint_url, secret, event_types, " +
            "is_active, created_at, updated_at " +
            "FROM webhook_subscriptions " +
            "WHERE tenant_id = @TenantId AND is_active = true AND event_types @> @EventTypeJson::jsonb",
            new { TenantId = tenantId, EventTypeJson = $"[\"{eventType}\"]" });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        var eventTypesJson = JsonSerializer.Serialize(subscription.EventTypes, PostgresJson.Ctx.IReadOnlyListString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO webhook_subscriptions (subscription_id, tenant_id, name, endpoint_url, " +
            "secret, event_types, is_active, created_at, updated_at) " +
            "VALUES (@SubscriptionId, @TenantId, @Name, @EndpointUrl, @Secret, " +
            "@EventTypes::jsonb, @IsActive, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (subscription_id) DO UPDATE SET " +
            "name = EXCLUDED.name, endpoint_url = EXCLUDED.endpoint_url, " +
            "secret = EXCLUDED.secret, event_types = EXCLUDED.event_types, " +
            "is_active = EXCLUDED.is_active, updated_at = EXCLUDED.updated_at",
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
            });
    }

    public async Task DeleteAsync(string subscriptionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM webhook_subscriptions WHERE subscription_id = @Id",
            new { Id = subscriptionId });
    }

    private sealed record SubscriptionRow(
        string subscription_id,
        string tenant_id,
        string name,
        string endpoint_url,
        string secret,
        string event_types,
        bool is_active,
        DateTimeOffset created_at,
        DateTimeOffset updated_at)
    {
        public WebhookSubscription ToModel()
        {
            var types = JsonSerializer.Deserialize(event_types, PostgresJson.Ctx.IReadOnlyListString)
                ?? (IReadOnlyList<string>)[];
            return new WebhookSubscription(
                subscription_id, tenant_id, name, endpoint_url, secret,
                types, is_active, created_at, updated_at);
        }
    }
}
