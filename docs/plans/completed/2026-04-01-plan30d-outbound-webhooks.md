# Plan 30D: Outbound Webhooks

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable tenants to subscribe to platform events with persistent delivery queue, exponential backoff, HMAC-SHA256 signing, and dead-letter queue.

**Architecture:** WebhookDispatcher subscribes to PlatformEventBus, filters by tenant subscriptions, persists deliveries. WebhookDeliveryService processes queue with retry. 13 tenant/management endpoints for subscription CRUD and delivery monitoring.

**Tech Stack:** .NET 10 Native AOT, System.Threading.Channels, HMAC-SHA256, Dapper.

**Spec:** `docs/superpowers/specs/2026-04-01-v130-integration-compliance-design.md` — Sub-project D.

**Prerequisite:** Plan 30C complete (GDPR migration applied).

---

## File Map

### New Files (Platform.Core — models + interfaces)

| File | Responsibility |
|------|----------------|
| `src/Asterisk.Platform.Core/Webhooks/WebhookSubscription.cs` | Subscription model |
| `src/Asterisk.Platform.Core/Webhooks/WebhookDelivery.cs` | Delivery model + WebhookDeliveryStatus enum |
| `src/Asterisk.Platform.Core/Webhooks/WebhookEventPayload.cs` | Wire-format payload envelope |
| `src/Asterisk.Platform.Core/Webhooks/IWebhookSubscriptionStore.cs` | Subscription store interface |
| `src/Asterisk.Platform.Core/Webhooks/IWebhookDeliveryStore.cs` | Delivery store interface |
| `src/Asterisk.Platform.Core/Webhooks/WebhookSignatureService.cs` | Static HMAC-SHA256 signature helper |
| `src/Asterisk.Platform.Core/Webhooks/WebhookEventTypes.cs` | Registry of valid event type strings |

### New Files (Storage.InMemory)

| File | Responsibility |
|------|----------------|
| `src/Asterisk.Platform.Storage.InMemory/InMemoryWebhookSubscriptionStore.cs` | ConcurrentDictionary-backed subscriptions |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryWebhookDeliveryStore.cs` | ConcurrentDictionary-backed deliveries |

### New Files (Storage.Postgres)

| File | Responsibility |
|------|----------------|
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresWebhookSubscriptionStore.cs` | Dapper/Npgsql subscription store |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresWebhookDeliveryStore.cs` | Dapper/Npgsql delivery store |
| `src/Asterisk.Platform.Storage.Postgres/Migrations/006_OutboundWebhooks.sql` | DDL for webhook tables |

### New Files (Platform.Api — services + endpoints)

| File | Responsibility |
|------|----------------|
| `src/Asterisk.Platform.Api/Services/WebhookDispatcher.cs` | Singleton, subscribes to PlatformEventBus, creates deliveries |
| `src/Asterisk.Platform.Api/Services/WebhookDeliveryService.cs` | IHostedService, processes delivery queue + retry poll |
| `src/Asterisk.Platform.Api/Endpoints/WebhookSubscriptionEndpoints.cs` | 8 tenant endpoints for subscription management |
| `src/Asterisk.Platform.Api/Endpoints/ManagementWebhookEndpoints.cs` | 2 platform admin endpoints for dead-letter management |
| `src/Asterisk.Platform.Api/Endpoints/WebhookEventTypeEndpoints.cs` | 1 endpoint listing available event types |

### New Files (Tests)

| File | Responsibility |
|------|----------------|
| `tests/Asterisk.Platform.Core.Tests/Webhooks/WebhookSignatureServiceTests.cs` | Signature computation + verification |
| `tests/Asterisk.Platform.Core.Tests/Webhooks/WebhookEventTypesTests.cs` | Registry completeness |
| `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryWebhookSubscriptionStoreTests.cs` | CRUD + active-by-event-type filtering |
| `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryWebhookDeliveryStoreTests.cs` | Pending retries, dead-letter, pagination |
| `tests/Asterisk.Platform.Api.Tests/Services/WebhookDispatcherTests.cs` | Event filtering, delivery creation |
| `tests/Asterisk.Platform.Api.Tests/Services/WebhookDeliveryServiceTests.cs` | Retry scheduling, backoff timing, dead-letter transition |
| `tests/Asterisk.Platform.Api.Tests/Endpoints/WebhookSubscriptionEndpointTests.cs` | Endpoint integration tests |
| `tests/Asterisk.Platform.Api.Tests/Endpoints/ManagementWebhookEndpointTests.cs` | Dead-letter endpoint tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register webhook stores |
| `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` | Register webhook stores |
| `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs` | Add `[JsonSerializable]` for `IReadOnlyList<string>` (event_types JSONB) — already present, verify |
| `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | Register all webhook DTOs |
| `src/Asterisk.Platform.Api/Program.cs` | Register WebhookDispatcher, WebhookDeliveryService, HttpClient("webhooks"), map 3 endpoint groups |

---

## Task 1: Domain Models + Interfaces

**Phase:** A (Foundation) — Batch together

**Files:**
- Create: `src/Asterisk.Platform.Core/Webhooks/WebhookSubscription.cs`
- Create: `src/Asterisk.Platform.Core/Webhooks/WebhookDelivery.cs`
- Create: `src/Asterisk.Platform.Core/Webhooks/WebhookEventPayload.cs`
- Create: `src/Asterisk.Platform.Core/Webhooks/IWebhookSubscriptionStore.cs`
- Create: `src/Asterisk.Platform.Core/Webhooks/IWebhookDeliveryStore.cs`
- Create: `src/Asterisk.Platform.Core/Webhooks/WebhookSignatureService.cs`
- Create: `src/Asterisk.Platform.Core/Webhooks/WebhookEventTypes.cs`

- [ ] **Step 1: Create WebhookSubscription model**

File: `src/Asterisk.Platform.Core/Webhooks/WebhookSubscription.cs`

```csharp
namespace Asterisk.Platform.Core.Webhooks;

public sealed record WebhookSubscription(
    string SubscriptionId,
    string TenantId,
    string Name,
    string EndpointUrl,
    string Secret,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

- [ ] **Step 2: Create WebhookDelivery model + status enum**

File: `src/Asterisk.Platform.Core/Webhooks/WebhookDelivery.cs`

```csharp
namespace Asterisk.Platform.Core.Webhooks;

public sealed record WebhookDelivery(
    string DeliveryId,
    string TenantId,
    string SubscriptionId,
    string EventType,
    string Payload,
    WebhookDeliveryStatus Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset? NextRetryAt,
    int? LastResponseCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,
    Failed,
    DeadLetter
}
```

- [ ] **Step 3: Create WebhookEventPayload envelope**

File: `src/Asterisk.Platform.Core/Webhooks/WebhookEventPayload.cs`

```csharp
namespace Asterisk.Platform.Core.Webhooks;

/// <summary>
/// JSON envelope sent to webhook endpoints via HTTP POST.
/// </summary>
public sealed record WebhookEventPayload(
    string Id,
    string Type,
    string TenantId,
    DateTimeOffset Timestamp,
    object Data);
```

- [ ] **Step 4: Create IWebhookSubscriptionStore interface**

File: `src/Asterisk.Platform.Core/Webhooks/IWebhookSubscriptionStore.cs`

```csharp
namespace Asterisk.Platform.Core.Webhooks;

public interface IWebhookSubscriptionStore
{
    Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct);
    Task SaveAsync(WebhookSubscription subscription, CancellationToken ct);
    Task DeleteAsync(string subscriptionId, CancellationToken ct);
}
```

- [ ] **Step 5: Create IWebhookDeliveryStore interface**

File: `src/Asterisk.Platform.Core/Webhooks/IWebhookDeliveryStore.cs`

```csharp
namespace Asterisk.Platform.Core.Webhooks;

public interface IWebhookDeliveryStore
{
    Task SaveAsync(WebhookDelivery delivery, CancellationToken ct);
    Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct);
    Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct);
    Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct);
}
```

- [ ] **Step 6: Create WebhookSignatureService static helper**

File: `src/Asterisk.Platform.Core/Webhooks/WebhookSignatureService.cs`

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Asterisk.Platform.Core.Webhooks;

/// <summary>
/// HMAC-SHA256 signature computation for outbound webhook deliveries.
/// </summary>
public static class WebhookSignatureService
{
    /// <summary>
    /// Computes an HMAC-SHA256 signature as a lowercase hex string.
    /// Format: HMAC-SHA256(secret, "{timestamp}.{body}")
    /// </summary>
    public static string ComputeSignature(string timestamp, string body, string secret)
        => Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    /// <summary>
    /// Verifies that a signature matches the expected value.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public static bool VerifySignature(string timestamp, string body, string secret, string signature)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(ComputeSignature(timestamp, body, secret)),
            Encoding.UTF8.GetBytes(signature));

    /// <summary>
    /// Generates a cryptographically random secret for new subscriptions.
    /// Returns a 32-byte hex string (64 characters).
    /// </summary>
    public static string GenerateSecret()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
```

- [ ] **Step 7: Create WebhookEventTypes registry**

File: `src/Asterisk.Platform.Core/Webhooks/WebhookEventTypes.cs`

Note: These must match the `Type` property values from `PlatformEventBus.cs` concrete event records. The spec's D2 table uses `agent_assist.*` but the actual PlatformEvent records use `agentassist.*`. We follow the source code — the actual event Type values.

```csharp
namespace Asterisk.Platform.Core.Webhooks;

/// <summary>
/// Registry of all supported webhook event type strings.
/// Values must match PlatformEvent.Type on concrete event records.
/// </summary>
public static class WebhookEventTypes
{
    public const string ConversationAssigned = "conversation.assigned";
    public const string ConversationMessage = "conversation.message";
    public const string ConversationStateChanged = "conversation.state_changed";
    public const string AgentStateChanged = "agent.state_changed";
    public const string CampaignStatusChanged = "campaign.status_changed";
    public const string CampaignMetricsUpdated = "campaign.metrics_updated";
    public const string CampaignDispositionSubmitted = "campaign.disposition_submitted";
    public const string AgentAssistSuggestion = "agentassist.suggestion";
    public const string AgentAssistSentiment = "agentassist.sentiment";
    public const string AgentAssistComplianceAlert = "agentassist.compliance_alert";
    public const string AgentAssistTranscript = "agentassist.transcript";

    /// <summary>Synthetic event type sent by POST /{id}/test endpoint.</summary>
    public const string WebhookTest = "webhook.test";

    /// <summary>All valid event types that tenants can subscribe to.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ConversationAssigned,
        ConversationMessage,
        ConversationStateChanged,
        AgentStateChanged,
        CampaignStatusChanged,
        CampaignMetricsUpdated,
        CampaignDispositionSubmitted,
        AgentAssistSuggestion,
        AgentAssistSentiment,
        AgentAssistComplianceAlert,
        AgentAssistTranscript,
    ];

    /// <summary>
    /// Event type descriptions for the /api/webhooks/event-types endpoint.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [ConversationAssigned] = "Fired when a conversation is assigned to an agent",
        [ConversationMessage] = "Fired when a new message arrives in a conversation",
        [ConversationStateChanged] = "Fired when a conversation changes state",
        [AgentStateChanged] = "Fired when an agent's presence state changes",
        [CampaignStatusChanged] = "Fired when an outbound campaign changes status",
        [CampaignMetricsUpdated] = "Fired when campaign dialing metrics are updated",
        [CampaignDispositionSubmitted] = "Fired when an agent submits a disposition for a campaign call",
        [AgentAssistSuggestion] = "Fired when an agent assist suggestion is generated",
        [AgentAssistSentiment] = "Fired when a sentiment reading is produced for a call",
        [AgentAssistComplianceAlert] = "Fired when a compliance rule violation is detected",
        [AgentAssistTranscript] = "Fired when a transcript segment is produced during a call",
    };

    /// <summary>Returns true if the event type is a valid subscribable type.</summary>
    public static bool IsValid(string eventType) => All.Contains(eventType);
}
```

- [ ] **Step 8: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Core/Asterisk.Platform.Core.csproj
```

---

## Task 2: InMemory Storage

**Phase:** A (Foundation) — Batch together

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryWebhookSubscriptionStore.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryWebhookDeliveryStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create InMemoryWebhookSubscriptionStore**

File: `src/Asterisk.Platform.Storage.InMemory/InMemoryWebhookSubscriptionStore.cs`

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly ConcurrentDictionary<string, WebhookSubscription> _subscriptions = new();

    public Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct)
    {
        _subscriptions.TryGetValue(subscriptionId, out var sub);
        return Task.FromResult(sub);
    }

    public Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        IReadOnlyList<WebhookSubscription> result = _subscriptions.Values
            .Where(s => string.Equals(s.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct)
    {
        IReadOnlyList<WebhookSubscription> result = _subscriptions.Values
            .Where(s => s.IsActive
                && string.Equals(s.TenantId, tenantId, StringComparison.Ordinal)
                && s.EventTypes.Contains(eventType))
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        _subscriptions[subscription.SubscriptionId] = subscription;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string subscriptionId, CancellationToken ct)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Create InMemoryWebhookDeliveryStore**

File: `src/Asterisk.Platform.Storage.InMemory/InMemoryWebhookDeliveryStore.cs`

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly ConcurrentDictionary<string, WebhookDelivery> _deliveries = new();

    public Task SaveAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        _deliveries[delivery.DeliveryId] = delivery;
        return Task.CompletedTask;
    }

    public Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct)
    {
        _deliveries.TryGetValue(deliveryId, out var delivery);
        return Task.FromResult(delivery);
    }

    public Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        IReadOnlyList<WebhookDelivery> result = _deliveries.Values
            .Where(d => d.Status == WebhookDeliveryStatus.Pending
                && d.NextRetryAt.HasValue
                && d.NextRetryAt.Value <= now)
            .OrderBy(d => d.NextRetryAt)
            .Take(batchSize)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        var filtered = _deliveries.Values
            .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<WebhookDelivery>(items, totalCount, page, pageSize));
    }

    public Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct)
    {
        var filtered = _deliveries.Values
            .Where(d => d.Status == WebhookDeliveryStatus.DeadLetter
                && string.Equals(d.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<WebhookDelivery>(items, totalCount, page, pageSize));
    }

    public Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        _deliveries[delivery.DeliveryId] = delivery;
        return Task.CompletedTask;
    }

    public Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        var toRemove = _deliveries.Values
            .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal))
            .Select(d => d.DeliveryId)
            .ToList();

        foreach (var id in toRemove)
            _deliveries.TryRemove(id, out _);

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Register webhook stores in AddInMemoryStorage()**

File: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`

Add `using Asterisk.Platform.Core.Webhooks;` to the top of the file.

Add before the `// MultiTenant` section:

```csharp
        // Webhooks
        services.AddSingleton<IWebhookSubscriptionStore, InMemoryWebhookSubscriptionStore>();
        services.AddSingleton<IWebhookDeliveryStore, InMemoryWebhookDeliveryStore>();
```

- [ ] **Step 4: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Storage.InMemory/Asterisk.Platform.Storage.InMemory.csproj
```

---

## Task 3: Postgres Storage

**Phase:** A (Foundation) — Batch together

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/006_OutboundWebhooks.sql`
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresWebhookSubscriptionStore.cs`
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresWebhookDeliveryStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create migration 006_OutboundWebhooks.sql**

File: `src/Asterisk.Platform.Storage.Postgres/Migrations/006_OutboundWebhooks.sql`

```sql
-- 006_OutboundWebhooks.sql
-- Outbound webhook subscription and delivery tables for Plan 30D

CREATE TABLE IF NOT EXISTS webhook_subscriptions (
    subscription_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    name VARCHAR(200) NOT NULL,
    endpoint_url VARCHAR(2000) NOT NULL,
    secret VARCHAR(64) NOT NULL,
    event_types JSONB NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_webhook_subscriptions_tenant
    ON webhook_subscriptions(tenant_id);

CREATE TABLE IF NOT EXISTS webhook_deliveries (
    delivery_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    subscription_id VARCHAR(36) NOT NULL REFERENCES webhook_subscriptions(subscription_id) ON DELETE CASCADE,
    event_type VARCHAR(100) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL DEFAULT 8,
    next_retry_at TIMESTAMPTZ,
    last_response_code INTEGER,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_pending
    ON webhook_deliveries(next_retry_at)
    WHERE status = 'Pending' AND next_retry_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_subscription
    ON webhook_deliveries(subscription_id);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_dead_letter
    ON webhook_deliveries(tenant_id)
    WHERE status = 'DeadLetter';
```

- [ ] **Step 2: Create PostgresWebhookSubscriptionStore**

File: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresWebhookSubscriptionStore.cs`

```csharp
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
```

- [ ] **Step 3: Create PostgresWebhookDeliveryStore**

File: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresWebhookDeliveryStore.cs`

```csharp
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
```

- [ ] **Step 4: Register webhook stores in AddPostgresStorage()**

File: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

Add `using Asterisk.Platform.Core.Webhooks;` to the top of the file.

Add before `// RBAC` section:

```csharp
        // Webhooks
        services.AddSingleton<IWebhookSubscriptionStore, PostgresWebhookSubscriptionStore>();
        services.AddSingleton<IWebhookDeliveryStore, PostgresWebhookDeliveryStore>();
```

- [ ] **Step 5: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Storage.Postgres/Asterisk.Platform.Storage.Postgres.csproj
```

---

## Task 4: WebhookDispatcher Service

**Phase:** B (Critical) — Individual focused subagent

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/WebhookDispatcher.cs`

- [ ] **Step 1: Create WebhookDispatcher**

File: `src/Asterisk.Platform.Api/Services/WebhookDispatcher.cs`

```csharp
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Subscribes to PlatformEventBus and creates WebhookDelivery records for matching subscriptions.
/// Enqueues new deliveries into a Channel for immediate processing by WebhookDeliveryService.
/// </summary>
internal sealed class WebhookDispatcher : IDisposable
{
    private readonly IWebhookSubscriptionStore _subscriptionStore;
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IClock _clock;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly Channel<WebhookDelivery> _channel;
    private IDisposable? _subscription;

    public WebhookDispatcher(
        PlatformEventBus eventBus,
        IWebhookSubscriptionStore subscriptionStore,
        IWebhookDeliveryStore deliveryStore,
        IClock clock,
        ILogger<WebhookDispatcher> logger)
    {
        _subscriptionStore = subscriptionStore;
        _deliveryStore = deliveryStore;
        _clock = clock;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WebhookDelivery>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        _subscription = eventBus.Events.Subscribe(OnEvent);
    }

    /// <summary>
    /// Channel reader for WebhookDeliveryService to consume new deliveries.
    /// </summary>
    public ChannelReader<WebhookDelivery> DeliveryReader => _channel.Reader;

    private async void OnEvent(PlatformEvent evt)
    {
        try
        {
            var subs = await _subscriptionStore.GetActiveByEventTypeAsync(
                evt.TenantId, evt.Type, CancellationToken.None);

            if (subs.Count == 0)
                return;

            var payload = SerializePayload(evt);

            foreach (var sub in subs)
            {
                var now = _clock.UtcNow;
                var delivery = new WebhookDelivery(
                    DeliveryId: Guid.NewGuid().ToString("N"),
                    TenantId: evt.TenantId,
                    SubscriptionId: sub.SubscriptionId,
                    EventType: evt.Type,
                    Payload: payload,
                    Status: WebhookDeliveryStatus.Pending,
                    Attempts: 0,
                    MaxAttempts: 8,
                    NextRetryAt: now,
                    LastResponseCode: null,
                    LastError: null,
                    CreatedAt: now,
                    DeliveredAt: null);

                await _deliveryStore.SaveAsync(delivery, CancellationToken.None);
                await _channel.Writer.WriteAsync(delivery, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogDispatchError(_logger, evt.Type, evt.TenantId, ex);
        }
    }

    private static string SerializePayload(PlatformEvent evt)
    {
        var envelope = new WebhookEventPayload(
            Id: Guid.NewGuid().ToString("N"),
            Type: evt.Type,
            TenantId: evt.TenantId,
            Timestamp: evt.Timestamp,
            Data: evt);

        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _channel.Writer.TryComplete();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to dispatch webhook for event {EventType} in tenant {TenantId}")]
    private static partial void LogDispatchError(ILogger logger, string eventType, string tenantId, Exception ex);
}
```

- [ ] **Step 2: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj
```

---

## Task 5: WebhookDeliveryService Background Worker

**Phase:** B (Critical) — Individual focused subagent

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/WebhookDeliveryService.cs`

- [ ] **Step 1: Create WebhookDeliveryService**

File: `src/Asterisk.Platform.Api/Services/WebhookDeliveryService.cs`

```csharp
using System.Net.Http.Headers;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Background service that delivers webhook HTTP POST requests with retry and dead-letter support.
/// Reads new deliveries from WebhookDispatcher's Channel and polls the store for pending retries.
/// </summary>
internal sealed class WebhookDeliveryService : BackgroundService
{
    private static readonly int[] BackoffSeconds = [0, 60, 300, 1800, 7200, 18000, 28800, 28800];
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly WebhookDispatcher _dispatcher;
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly IWebhookSubscriptionStore _subscriptionStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly ILogger<WebhookDeliveryService> _logger;

    public WebhookDeliveryService(
        WebhookDispatcher dispatcher,
        IWebhookDeliveryStore deliveryStore,
        IWebhookSubscriptionStore subscriptionStore,
        IHttpClientFactory httpClientFactory,
        IClock clock,
        ILogger<WebhookDeliveryService> logger)
    {
        _dispatcher = dispatcher;
        _deliveryStore = deliveryStore;
        _subscriptionStore = subscriptionStore;
        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run both loops concurrently: channel reader + DB poller
        var channelTask = ProcessChannelAsync(stoppingToken);
        var pollTask = PollPendingRetriesAsync(stoppingToken);

        await Task.WhenAll(channelTask, pollTask);
    }

    private async Task ProcessChannelAsync(CancellationToken ct)
    {
        await foreach (var delivery in _dispatcher.DeliveryReader.ReadAllAsync(ct))
        {
            await DeliverAsync(delivery, ct);
        }
    }

    private async Task PollPendingRetriesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryPollInterval, ct);

                var pending = await _deliveryStore.ListPendingRetriesAsync(
                    _clock.UtcNow, batchSize: 100, ct);

                foreach (var delivery in pending)
                {
                    if (ct.IsCancellationRequested) break;
                    await DeliverAsync(delivery, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPollError(_logger, ex);
            }
        }
    }

    private async Task DeliverAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        try
        {
            var sub = await _subscriptionStore.GetByIdAsync(delivery.SubscriptionId, ct);
            if (sub is null)
            {
                // Subscription deleted — mark as dead letter
                var orphaned = delivery with
                {
                    Status = WebhookDeliveryStatus.DeadLetter,
                    LastError = "Subscription deleted",
                };
                await _deliveryStore.UpdateAsync(orphaned, ct);
                return;
            }

            var client = _httpClientFactory.CreateClient("webhooks");
            client.Timeout = HttpTimeout;

            var timestamp = _clock.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = WebhookSignatureService.ComputeSignature(timestamp, delivery.Payload, sub.Secret);

            using var request = new HttpRequestMessage(HttpMethod.Post, sub.EndpointUrl);
            request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Webhook-Id", delivery.DeliveryId);
            request.Headers.Add("X-Webhook-Event", delivery.EventType);
            request.Headers.Add("X-Webhook-Timestamp", timestamp);
            request.Headers.Add("X-Webhook-Signature", signature);

            using var response = await client.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                var delivered = delivery with
                {
                    Status = WebhookDeliveryStatus.Delivered,
                    Attempts = delivery.Attempts + 1,
                    LastResponseCode = statusCode,
                    LastError = null,
                    NextRetryAt = null,
                    DeliveredAt = _clock.UtcNow,
                };
                await _deliveryStore.UpdateAsync(delivered, ct);
                LogDeliverySuccess(_logger, delivery.DeliveryId, sub.EndpointUrl, statusCode);
            }
            else
            {
                await HandleFailureAsync(delivery, statusCode, $"HTTP {statusCode}", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — leave delivery in current state for next startup
        }
        catch (TaskCanceledException)
        {
            // HTTP timeout
            await HandleFailureAsync(delivery, null, "Request timeout", ct);
        }
        catch (HttpRequestException ex)
        {
            await HandleFailureAsync(delivery, null, $"Network error: {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            LogDeliveryError(_logger, delivery.DeliveryId, ex);
            await HandleFailureAsync(delivery, null, $"Unexpected error: {ex.Message}", ct);
        }
    }

    private async Task HandleFailureAsync(
        WebhookDelivery delivery, int? responseCode, string error, CancellationToken ct)
    {
        var newAttempts = delivery.Attempts + 1;

        if (newAttempts >= delivery.MaxAttempts)
        {
            var deadLetter = delivery with
            {
                Status = WebhookDeliveryStatus.DeadLetter,
                Attempts = newAttempts,
                LastResponseCode = responseCode,
                LastError = error,
                NextRetryAt = null,
            };
            await _deliveryStore.UpdateAsync(deadLetter, ct);
            LogDeadLetter(_logger, delivery.DeliveryId, newAttempts);
        }
        else
        {
            var backoffIndex = Math.Min(newAttempts, BackoffSeconds.Length - 1);
            var nextRetry = _clock.UtcNow.AddSeconds(BackoffSeconds[backoffIndex]);

            var retry = delivery with
            {
                Status = WebhookDeliveryStatus.Pending,
                Attempts = newAttempts,
                LastResponseCode = responseCode,
                LastError = error,
                NextRetryAt = nextRetry,
            };
            await _deliveryStore.UpdateAsync(retry, ct);
            LogRetryScheduled(_logger, delivery.DeliveryId, newAttempts, nextRetry);
        }
    }

    /// <summary>
    /// Backoff schedule for external consumers (e.g., tests).
    /// </summary>
    internal static int GetBackoffSeconds(int attemptNumber)
    {
        var index = Math.Min(attemptNumber, BackoffSeconds.Length - 1);
        return BackoffSeconds[index];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error polling pending webhook retries")]
    private static partial void LogPollError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {DeliveryId} delivered to {Url} (HTTP {StatusCode})")]
    private static partial void LogDeliverySuccess(ILogger logger, string deliveryId, string url, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery {DeliveryId} failed unexpectedly")]
    private static partial void LogDeliveryError(ILogger logger, string deliveryId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {DeliveryId} moved to dead letter after {Attempts} attempts")]
    private static partial void LogDeadLetter(ILogger logger, string deliveryId, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {DeliveryId} retry #{Attempts} scheduled for {NextRetry}")]
    private static partial void LogRetryScheduled(ILogger logger, string deliveryId, int attempts, DateTimeOffset nextRetry);
}
```

- [ ] **Step 2: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj
```

---

## Task 6: Tenant Endpoints — WebhookSubscriptionEndpoints

**Phase:** B (Critical) — Individual focused subagent

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/WebhookSubscriptionEndpoints.cs`

- [ ] **Step 1: Create WebhookSubscriptionEndpoints**

File: `src/Asterisk.Platform.Api/Endpoints/WebhookSubscriptionEndpoints.cs`

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class WebhookSubscriptionEndpoints
{
    public static void MapWebhookSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks/subscriptions").RequireAuthorization("Authenticated");
        group.MapGet("/", ListSubscriptions);
        group.MapPost("/", CreateSubscription);
        group.MapGet("/{id}", GetSubscription);
        group.MapPut("/{id}", UpdateSubscription);
        group.MapDelete("/{id}", DeleteSubscription);
        group.MapPost("/{id}/test", TestSubscription);
        group.MapGet("/{id}/deliveries", ListDeliveries);
        group.MapPost("/{id}/rotate-secret", RotateSecret);
    }

    private static async Task<IResult> ListSubscriptions(
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var subs = await store.ListByTenantAsync(tenantId, ct);
        return Results.Ok(subs.Select(MaskSecret).ToList());
    }

    private static async Task<IResult> CreateSubscription(
        HttpContext context,
        [FromBody] CreateWebhookSubscriptionRequest body,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new ErrorResponse("Name is required"));

        if (string.IsNullOrWhiteSpace(body.EndpointUrl))
            return Results.BadRequest(new ErrorResponse("EndpointUrl is required"));

        if (!Uri.TryCreate(body.EndpointUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse("EndpointUrl must be a valid HTTPS URL"));

        if (body.EventTypes is null || body.EventTypes.Count == 0)
            return Results.BadRequest(new ErrorResponse("At least one event type is required"));

        var invalid = body.EventTypes.Where(e => !WebhookEventTypes.IsValid(e)).ToList();
        if (invalid.Count > 0)
            return Results.BadRequest(new ErrorDetailResponse(
                "Invalid event types", invalid));

        var tenantId = GetTenantId(context);
        var now = clock.UtcNow;
        var subscription = new WebhookSubscription(
            SubscriptionId: Guid.NewGuid().ToString("N"),
            TenantId: tenantId,
            Name: body.Name,
            EndpointUrl: body.EndpointUrl,
            Secret: WebhookSignatureService.GenerateSecret(),
            EventTypes: body.EventTypes,
            IsActive: true,
            CreatedAt: now,
            UpdatedAt: now);

        await store.SaveAsync(subscription, ct);

        // Return with secret visible on creation only
        return Results.Created(
            $"/api/webhooks/subscriptions/{subscription.SubscriptionId}",
            subscription);
    }

    private static async Task<IResult> GetSubscription(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        return Results.Ok(MaskSecret(sub));
    }

    private static async Task<IResult> UpdateSubscription(
        string id,
        HttpContext context,
        [FromBody] UpdateWebhookSubscriptionRequest body,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        if (body.EndpointUrl is not null)
        {
            if (!Uri.TryCreate(body.EndpointUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new ErrorResponse("EndpointUrl must be a valid HTTPS URL"));
        }

        if (body.EventTypes is not null)
        {
            if (body.EventTypes.Count == 0)
                return Results.BadRequest(new ErrorResponse("At least one event type is required"));

            var invalid = body.EventTypes.Where(e => !WebhookEventTypes.IsValid(e)).ToList();
            if (invalid.Count > 0)
                return Results.BadRequest(new ErrorDetailResponse(
                    "Invalid event types", invalid));
        }

        var updated = sub with
        {
            Name = body.Name ?? sub.Name,
            EndpointUrl = body.EndpointUrl ?? sub.EndpointUrl,
            EventTypes = body.EventTypes ?? sub.EventTypes,
            IsActive = body.IsActive ?? sub.IsActive,
            UpdatedAt = clock.UtcNow,
        };

        await store.SaveAsync(updated, ct);
        return Results.Ok(MaskSecret(updated));
    }

    private static async Task<IResult> DeleteSubscription(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IWebhookDeliveryStore deliveryStore,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        await deliveryStore.DeleteBySubscriptionAsync(id, ct);
        await store.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestSubscription(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IWebhookDeliveryStore deliveryStore,
        [FromServices] WebhookDispatcher dispatcher,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var now = clock.UtcNow;
        var testPayload = System.Text.Json.JsonSerializer.Serialize(new WebhookEventPayload(
            Id: Guid.NewGuid().ToString("N"),
            Type: WebhookEventTypes.WebhookTest,
            TenantId: tenantId,
            Timestamp: now,
            Data: new { message = "This is a test webhook delivery" }),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

        var delivery = new WebhookDelivery(
            DeliveryId: Guid.NewGuid().ToString("N"),
            TenantId: tenantId,
            SubscriptionId: sub.SubscriptionId,
            EventType: WebhookEventTypes.WebhookTest,
            Payload: testPayload,
            Status: WebhookDeliveryStatus.Pending,
            Attempts: 0,
            MaxAttempts: 1,
            NextRetryAt: now,
            LastResponseCode: null,
            LastError: null,
            CreatedAt: now,
            DeliveredAt: null);

        await deliveryStore.SaveAsync(delivery, ct);
        await dispatcher.DeliveryReader.ReadAsync(ct); // Consume to avoid; just enqueue directly
        // Actually, write to channel instead:
        // The test endpoint saves the delivery and lets the delivery service pick it up.
        // We just save it — the poll loop or channel will process it.

        return Results.Ok(new MessageResponse($"Test event queued as delivery {delivery.DeliveryId}"));
    }

    private static async Task<IResult> ListDeliveries(
        string id,
        HttpContext context,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromServices] IWebhookSubscriptionStore subStore,
        [FromServices] IWebhookDeliveryStore deliveryStore,
        CancellationToken ct)
    {
        var sub = await subStore.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var p = page > 0 ? page : 1;
        var ps = pageSize > 0 ? Math.Min(pageSize, 100) : 20;

        var result = await deliveryStore.ListBySubscriptionAsync(id, p, ps, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RotateSecret(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var updated = sub with
        {
            Secret = WebhookSignatureService.GenerateSecret(),
            UpdatedAt = clock.UtcNow,
        };

        await store.SaveAsync(updated, ct);

        // Return with new secret visible after rotation
        return Results.Ok(updated);
    }

    private static WebhookSubscription MaskSecret(WebhookSubscription sub)
        => sub with { Secret = $"{sub.Secret[..8]}...{sub.Secret[^4..]}" };

    private static string GetTenantId(HttpContext context)
        => context.Items["TenantId"] is TenantId tid
            ? tid.Value
            : throw new InvalidOperationException("TenantId not resolved");
}

// ─── Request DTOs ────────────────────────────────────────────────────────────

internal sealed record CreateWebhookSubscriptionRequest(
    string Name,
    string EndpointUrl,
    IReadOnlyList<string> EventTypes);

internal sealed record UpdateWebhookSubscriptionRequest(
    string? Name,
    string? EndpointUrl,
    IReadOnlyList<string>? EventTypes,
    bool? IsActive);
```

**Important fix needed in Step 1:** The `TestSubscription` method has a bug — it reads from the channel reader which would consume an unrelated delivery. Fix: just save the delivery to the store. The poll loop will pick it up within 30 seconds, or for immediate delivery, write directly to the channel writer via a public method on the dispatcher.

Revised `TestSubscription` — replace the channel read/write lines with:

```csharp
        // Save to store — the poll loop will pick it up, or if we want immediate:
        // The delivery service's retry poll will process it within 30s.
        // For test purposes, this is acceptable. No need to write to channel directly.

        return Results.Ok(new MessageResponse($"Test event queued as delivery {delivery.DeliveryId}"));
```

- [ ] **Step 2: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj
```

---

## Task 7: Management + Event Type Endpoints

**Phase:** B (Critical) — Batch with Task 6 if time permits

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementWebhookEndpoints.cs`
- Create: `src/Asterisk.Platform.Api/Endpoints/WebhookEventTypeEndpoints.cs`

- [ ] **Step 1: Create ManagementWebhookEndpoints**

File: `src/Asterisk.Platform.Api/Endpoints/ManagementWebhookEndpoints.cs`

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementWebhookEndpoints
{
    public static void MapManagementWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/webhooks").RequireAuthorization("PlatformAdminOnly");
        group.MapGet("/dead-letter", ListDeadLetter);
        group.MapPost("/dead-letter/{id}/retry", RetryDeadLetter);
    }

    private static async Task<IResult> ListDeadLetter(
        [FromQuery] string tenantId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromServices] IWebhookDeliveryStore store,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return Results.BadRequest(new ErrorResponse("tenantId query parameter is required"));

        var p = page > 0 ? page : 1;
        var ps = pageSize > 0 ? Math.Min(pageSize, 100) : 20;

        var result = await store.ListDeadLetterAsync(tenantId, p, ps, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RetryDeadLetter(
        string id,
        [FromServices] IWebhookDeliveryStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var delivery = await store.GetByIdAsync(id, ct);
        if (delivery is null)
            return Results.NotFound();

        if (delivery.Status != WebhookDeliveryStatus.DeadLetter)
            return Results.BadRequest(new ErrorResponse("Only dead-letter deliveries can be retried"));

        var retried = delivery with
        {
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            MaxAttempts = 8,
            NextRetryAt = clock.UtcNow,
            LastError = null,
            LastResponseCode = null,
            DeliveredAt = null,
        };

        await store.UpdateAsync(retried, ct);
        return Results.Ok(new MessageResponse($"Delivery {id} re-enqueued for retry"));
    }
}
```

- [ ] **Step 2: Create WebhookEventTypeEndpoints**

File: `src/Asterisk.Platform.Api/Endpoints/WebhookEventTypeEndpoints.cs`

```csharp
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Api.Endpoints;

internal static class WebhookEventTypeEndpoints
{
    public static void MapWebhookEventTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks/event-types").RequireAuthorization("Authenticated");
        group.MapGet("/", ListEventTypes);
    }

    private static IResult ListEventTypes()
    {
        var types = WebhookEventTypes.All.Select(t => new WebhookEventTypeDto(
            t,
            WebhookEventTypes.Descriptions.TryGetValue(t, out var desc) ? desc : ""));
        return Results.Ok(types.ToList());
    }
}

internal sealed record WebhookEventTypeDto(string EventType, string Description);
```

- [ ] **Step 3: Build and verify zero warnings**

```bash
dotnet build src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj
```

---

## Task 8: ApiJsonContext + Program.cs Wiring

**Phase:** C (Integration) — Batch together

**Files:**
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Register webhook DTOs in ApiJsonContext**

File: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

Add `using Asterisk.Platform.Core.Webhooks;` to the top of the file.

Add before `[JsonSourceGenerationOptions(` line:

```csharp
// Webhooks
[JsonSerializable(typeof(WebhookSubscription))]
[JsonSerializable(typeof(List<WebhookSubscription>))]
[JsonSerializable(typeof(WebhookDelivery))]
[JsonSerializable(typeof(List<WebhookDelivery>))]
[JsonSerializable(typeof(PagedResult<WebhookDelivery>))]
[JsonSerializable(typeof(WebhookEventPayload))]
[JsonSerializable(typeof(WebhookDeliveryStatus))]
[JsonSerializable(typeof(CreateWebhookSubscriptionRequest))]
[JsonSerializable(typeof(UpdateWebhookSubscriptionRequest))]
[JsonSerializable(typeof(WebhookEventTypeDto))]
[JsonSerializable(typeof(List<WebhookEventTypeDto>))]
```

- [ ] **Step 2: Wire services and endpoints in Program.cs**

File: `src/Asterisk.Platform.Api/Program.cs`

Add `using Asterisk.Platform.Core.Webhooks;` to the usings (if not already covered by other usings pulling in the namespace).

**2a.** After the `builder.Services.AddSingleton<ScheduledReportStore>();` line (~line 93), add:

```csharp
// ─── Outbound Webhooks ──────────────────────────────────────────────────────
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService<WebhookDeliveryService>();
builder.Services.AddHttpClient("webhooks");
```

**2b.** After `app.MapManagementImpersonationEndpoints();` line (~line 324), add:

```csharp
app.MapWebhookSubscriptionEndpoints();
app.MapManagementWebhookEndpoints();
app.MapWebhookEventTypeEndpoints();
```

- [ ] **Step 3: Build entire solution and verify zero warnings**

```bash
dotnet build Asterisk.Platform.slnx
```

---

## Task 9: Unit Tests — Models + Signature + Registry

**Phase:** C (Integration) — Batch together

**Files:**
- Create: `tests/Asterisk.Platform.Core.Tests/Webhooks/WebhookSignatureServiceTests.cs`
- Create: `tests/Asterisk.Platform.Core.Tests/Webhooks/WebhookEventTypesTests.cs`

- [ ] **Step 1: Create WebhookSignatureServiceTests**

File: `tests/Asterisk.Platform.Core.Tests/Webhooks/WebhookSignatureServiceTests.cs`

```csharp
using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Core.Tests.Webhooks;

public class WebhookSignatureServiceTests
{
    [Fact]
    public void ComputeSignature_ShouldReturnConsistentHex_WhenCalledWithSameInputs()
    {
        var sig1 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret123");
        var sig2 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret123");
        sig1.Should().Be(sig2);
    }

    [Fact]
    public void ComputeSignature_ShouldReturnDifferentValues_WhenSecretDiffers()
    {
        var sig1 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret-a");
        var sig2 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret-b");
        sig1.Should().NotBe(sig2);
    }

    [Fact]
    public void ComputeSignature_ShouldReturnDifferentValues_WhenTimestampDiffers()
    {
        var sig1 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        var sig2 = WebhookSignatureService.ComputeSignature("1712000001", "{}", "secret");
        sig1.Should().NotBe(sig2);
    }

    [Fact]
    public void ComputeSignature_ShouldReturnLowercaseHex()
    {
        var sig = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        sig.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void VerifySignature_ShouldReturnTrue_WhenSignatureMatches()
    {
        var sig = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        WebhookSignatureService.VerifySignature("1712000000", "{}", "secret", sig).Should().BeTrue();
    }

    [Fact]
    public void VerifySignature_ShouldReturnFalse_WhenSignatureTampered()
    {
        var sig = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        WebhookSignatureService.VerifySignature("1712000000", "{}", "secret", sig + "ff").Should().BeFalse();
    }

    [Fact]
    public void GenerateSecret_ShouldReturn64CharHexString()
    {
        var secret = WebhookSignatureService.GenerateSecret();
        secret.Should().HaveLength(64);
        secret.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void GenerateSecret_ShouldReturnUniqueValues()
    {
        var s1 = WebhookSignatureService.GenerateSecret();
        var s2 = WebhookSignatureService.GenerateSecret();
        s1.Should().NotBe(s2);
    }
}
```

- [ ] **Step 2: Create WebhookEventTypesTests**

File: `tests/Asterisk.Platform.Core.Tests/Webhooks/WebhookEventTypesTests.cs`

```csharp
using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Core.Tests.Webhooks;

public class WebhookEventTypesTests
{
    [Fact]
    public void All_ShouldContain11EventTypes()
    {
        WebhookEventTypes.All.Should().HaveCount(11);
    }

    [Fact]
    public void IsValid_ShouldReturnTrue_WhenEventTypeIsInRegistry()
    {
        WebhookEventTypes.IsValid("conversation.assigned").Should().BeTrue();
        WebhookEventTypes.IsValid("agent.state_changed").Should().BeTrue();
        WebhookEventTypes.IsValid("agentassist.suggestion").Should().BeTrue();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenEventTypeIsNotInRegistry()
    {
        WebhookEventTypes.IsValid("nonexistent.event").Should().BeFalse();
        WebhookEventTypes.IsValid("webhook.test").Should().BeFalse(); // Test event is not subscribable
    }

    [Fact]
    public void Descriptions_ShouldHaveEntryForEveryEventType()
    {
        foreach (var eventType in WebhookEventTypes.All)
        {
            WebhookEventTypes.Descriptions.Should().ContainKey(eventType);
            WebhookEventTypes.Descriptions[eventType].Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData(WebhookEventTypes.ConversationAssigned, "conversation.assigned")]
    [InlineData(WebhookEventTypes.ConversationMessage, "conversation.message")]
    [InlineData(WebhookEventTypes.ConversationStateChanged, "conversation.state_changed")]
    [InlineData(WebhookEventTypes.AgentStateChanged, "agent.state_changed")]
    [InlineData(WebhookEventTypes.CampaignStatusChanged, "campaign.status_changed")]
    [InlineData(WebhookEventTypes.CampaignMetricsUpdated, "campaign.metrics_updated")]
    [InlineData(WebhookEventTypes.CampaignDispositionSubmitted, "campaign.disposition_submitted")]
    [InlineData(WebhookEventTypes.AgentAssistSuggestion, "agentassist.suggestion")]
    [InlineData(WebhookEventTypes.AgentAssistSentiment, "agentassist.sentiment")]
    [InlineData(WebhookEventTypes.AgentAssistComplianceAlert, "agentassist.compliance_alert")]
    [InlineData(WebhookEventTypes.AgentAssistTranscript, "agentassist.transcript")]
    public void Constants_ShouldMatchExpectedValues(string constant, string expected)
    {
        constant.Should().Be(expected);
    }
}
```

- [ ] **Step 3: Run Core tests**

```bash
dotnet test tests/Asterisk.Platform.Core.Tests/ -v q
```

---

## Task 10: Unit Tests — InMemory Stores

**Phase:** C (Integration) — Batch together

**Files:**
- Create: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryWebhookSubscriptionStoreTests.cs`
- Create: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryWebhookDeliveryStoreTests.cs`

- [ ] **Step 1: Create InMemoryWebhookSubscriptionStoreTests**

File: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryWebhookSubscriptionStoreTests.cs`

```csharp
using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryWebhookSubscriptionStoreTests
{
    private readonly InMemoryWebhookSubscriptionStore _store = new();

    private static WebhookSubscription CreateSubscription(
        string? id = null, string tenantId = "t1", bool isActive = true,
        IReadOnlyList<string>? eventTypes = null) => new(
        SubscriptionId: id ?? Guid.NewGuid().ToString("N"),
        TenantId: tenantId,
        Name: "Test Webhook",
        EndpointUrl: "https://example.com/webhook",
        Secret: "test-secret-1234567890123456789012345678901234567890",
        EventTypes: eventTypes ?? ["conversation.message"],
        IsActive: isActive,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveAsync_ShouldPersist_WhenCalledWithNewSubscription()
    {
        var sub = CreateSubscription();
        await _store.SaveAsync(sub, CancellationToken.None);

        var result = await _store.GetByIdAsync(sub.SubscriptionId, CancellationToken.None);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Webhook");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetByIdAsync("nonexistent", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListByTenantAsync_ShouldReturnOnlyTenantSubscriptions()
    {
        await _store.SaveAsync(CreateSubscription(tenantId: "t1"), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(tenantId: "t1"), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(tenantId: "t2"), CancellationToken.None);

        var result = await _store.ListByTenantAsync("t1", CancellationToken.None);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetActiveByEventTypeAsync_ShouldFilterByActiveAndEventType()
    {
        await _store.SaveAsync(CreateSubscription(id: "s1", isActive: true,
            eventTypes: ["conversation.message", "agent.state_changed"]), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(id: "s2", isActive: false,
            eventTypes: ["conversation.message"]), CancellationToken.None);
        await _store.SaveAsync(CreateSubscription(id: "s3", isActive: true,
            eventTypes: ["campaign.status_changed"]), CancellationToken.None);

        var result = await _store.GetActiveByEventTypeAsync("t1", "conversation.message", CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].SubscriptionId.Should().Be("s1");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveSubscription()
    {
        var sub = CreateSubscription(id: "to-delete");
        await _store.SaveAsync(sub, CancellationToken.None);
        await _store.DeleteAsync("to-delete", CancellationToken.None);

        var result = await _store.GetByIdAsync("to-delete", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldUpsert_WhenSubscriptionAlreadyExists()
    {
        var sub = CreateSubscription(id: "upsert");
        await _store.SaveAsync(sub, CancellationToken.None);

        var updated = sub with { Name = "Updated" };
        await _store.SaveAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync("upsert", CancellationToken.None);
        result!.Name.Should().Be("Updated");
    }
}
```

- [ ] **Step 2: Create InMemoryWebhookDeliveryStoreTests**

File: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryWebhookDeliveryStoreTests.cs`

```csharp
using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryWebhookDeliveryStoreTests
{
    private readonly InMemoryWebhookDeliveryStore _store = new();

    private static WebhookDelivery CreateDelivery(
        string? id = null, string subId = "sub1", string tenantId = "t1",
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending,
        DateTimeOffset? nextRetryAt = null, int attempts = 0) => new(
        DeliveryId: id ?? Guid.NewGuid().ToString("N"),
        TenantId: tenantId,
        SubscriptionId: subId,
        EventType: "conversation.message",
        Payload: "{}",
        Status: status,
        Attempts: attempts,
        MaxAttempts: 8,
        NextRetryAt: nextRetryAt,
        LastResponseCode: null,
        LastError: null,
        CreatedAt: DateTimeOffset.UtcNow,
        DeliveredAt: null);

    [Fact]
    public async Task SaveAsync_ShouldPersist_WhenCalledWithNewDelivery()
    {
        var delivery = CreateDelivery(id: "d1");
        await _store.SaveAsync(delivery, CancellationToken.None);

        var result = await _store.GetByIdAsync("d1", CancellationToken.None);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ListPendingRetriesAsync_ShouldReturnOnlyDueDeliveries()
    {
        var now = DateTimeOffset.UtcNow;

        await _store.SaveAsync(CreateDelivery(id: "due", nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "future", nextRetryAt: now.AddMinutes(10)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "delivered",
            status: WebhookDeliveryStatus.Delivered, nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "no-retry",
            status: WebhookDeliveryStatus.Pending, nextRetryAt: null), CancellationToken.None);

        var result = await _store.ListPendingRetriesAsync(now, 100, CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].DeliveryId.Should().Be("due");
    }

    [Fact]
    public async Task ListPendingRetriesAsync_ShouldRespectBatchSize()
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
            await _store.SaveAsync(CreateDelivery(nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);

        var result = await _store.ListPendingRetriesAsync(now, 3, CancellationToken.None);
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListDeadLetterAsync_ShouldReturnPaginatedDeadLetters()
    {
        for (int i = 0; i < 5; i++)
            await _store.SaveAsync(CreateDelivery(tenantId: "t1",
                status: WebhookDeliveryStatus.DeadLetter), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(tenantId: "t2",
            status: WebhookDeliveryStatus.DeadLetter), CancellationToken.None);

        var result = await _store.ListDeadLetterAsync("t1", 1, 3, CancellationToken.None);
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceDelivery()
    {
        var delivery = CreateDelivery(id: "upd");
        await _store.SaveAsync(delivery, CancellationToken.None);

        var updated = delivery with { Status = WebhookDeliveryStatus.Delivered };
        await _store.UpdateAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync("upd", CancellationToken.None);
        result!.Status.Should().Be(WebhookDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task DeleteBySubscriptionAsync_ShouldRemoveAllDeliveriesForSubscription()
    {
        await _store.SaveAsync(CreateDelivery(id: "d1", subId: "sub-del"), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "d2", subId: "sub-del"), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "d3", subId: "sub-keep"), CancellationToken.None);

        await _store.DeleteBySubscriptionAsync("sub-del", CancellationToken.None);

        (await _store.GetByIdAsync("d1", CancellationToken.None)).Should().BeNull();
        (await _store.GetByIdAsync("d2", CancellationToken.None)).Should().BeNull();
        (await _store.GetByIdAsync("d3", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListBySubscriptionAsync_ShouldReturnPaginatedResults()
    {
        for (int i = 0; i < 5; i++)
            await _store.SaveAsync(CreateDelivery(subId: "sub-page"), CancellationToken.None);

        var result = await _store.ListBySubscriptionAsync("sub-page", 1, 3, CancellationToken.None);
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
    }
}
```

- [ ] **Step 3: Run InMemory storage tests**

```bash
dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ -v q
```

---

## Task 11: Unit Tests — WebhookDispatcher + WebhookDeliveryService

**Phase:** C (Integration) — Batch together

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/Services/WebhookDispatcherTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/Services/WebhookDeliveryServiceTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/Endpoints/WebhookSubscriptionEndpointTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/Endpoints/ManagementWebhookEndpointTests.cs`

- [ ] **Step 1: Create WebhookDispatcherTests**

File: `tests/Asterisk.Platform.Api.Tests/Services/WebhookDispatcherTests.cs`

```csharp
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Services;

public class WebhookDispatcherTests : IDisposable
{
    private readonly PlatformEventBus _eventBus = new();
    private readonly IWebhookSubscriptionStore _subStore = Substitute.For<IWebhookSubscriptionStore>();
    private readonly IWebhookDeliveryStore _deliveryStore = Substitute.For<IWebhookDeliveryStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly WebhookDispatcher _dispatcher;

    public WebhookDispatcherTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _dispatcher = new WebhookDispatcher(
            _eventBus, _subStore, _deliveryStore, _clock,
            NullLogger<WebhookDispatcher>.Instance);
    }

    [Fact]
    public async Task OnEvent_ShouldCreateDelivery_WhenMatchingSubscriptionExists()
    {
        var sub = new WebhookSubscription("s1", "t1", "Test", "https://example.com/hook",
            "secret", ["conversation.message"], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _subStore.GetActiveByEventTypeAsync("t1", "conversation.message", Arg.Any<CancellationToken>())
            .Returns([sub]);

        _eventBus.Publish(new ConversationMessageEvent("t1", "c1", "m1", "user", "hello"));

        // Allow async subscription to process
        await Task.Delay(100);

        await _deliveryStore.Received(1).SaveAsync(
            Arg.Is<WebhookDelivery>(d =>
                d.SubscriptionId == "s1" &&
                d.EventType == "conversation.message" &&
                d.Status == WebhookDeliveryStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnEvent_ShouldNotCreateDelivery_WhenNoMatchingSubscription()
    {
        _subStore.GetActiveByEventTypeAsync("t1", "conversation.message", Arg.Any<CancellationToken>())
            .Returns(new List<WebhookSubscription>());

        _eventBus.Publish(new ConversationMessageEvent("t1", "c1", "m1", "user", "hello"));

        await Task.Delay(100);

        await _deliveryStore.DidNotReceive().SaveAsync(
            Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnEvent_ShouldCreateMultipleDeliveries_WhenMultipleSubscriptionsMatch()
    {
        var sub1 = new WebhookSubscription("s1", "t1", "Hook 1", "https://a.com/hook",
            "secret1", ["conversation.message"], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var sub2 = new WebhookSubscription("s2", "t1", "Hook 2", "https://b.com/hook",
            "secret2", ["conversation.message"], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _subStore.GetActiveByEventTypeAsync("t1", "conversation.message", Arg.Any<CancellationToken>())
            .Returns(new List<WebhookSubscription> { sub1, sub2 });

        _eventBus.Publish(new ConversationMessageEvent("t1", "c1", "m1", "user", "hello"));

        await Task.Delay(100);

        await _deliveryStore.Received(2).SaveAsync(
            Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _eventBus.Dispose();
    }
}
```

- [ ] **Step 2: Create WebhookDeliveryServiceTests**

File: `tests/Asterisk.Platform.Api.Tests/Services/WebhookDeliveryServiceTests.cs`

```csharp
using Asterisk.Platform.Api.Services;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests.Services;

public class WebhookDeliveryServiceTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(2, 300)]
    [InlineData(3, 1800)]
    [InlineData(4, 7200)]
    [InlineData(5, 18000)]
    [InlineData(6, 28800)]
    [InlineData(7, 28800)]
    [InlineData(100, 28800)] // Beyond array bounds clamps to last
    public void GetBackoffSeconds_ShouldReturnExpectedDelay(int attempt, int expectedSeconds)
    {
        WebhookDeliveryService.GetBackoffSeconds(attempt).Should().Be(expectedSeconds);
    }

    [Fact]
    public void BackoffSchedule_ShouldTotalApproximately24Hours()
    {
        var totalSeconds = 0;
        for (int i = 0; i < 8; i++)
            totalSeconds += WebhookDeliveryService.GetBackoffSeconds(i);

        // Total should be ~84660 seconds (~23.5 hours)
        totalSeconds.Should().BeInRange(80000, 90000);
    }
}
```

- [ ] **Step 3: Run API tests**

```bash
dotnet test tests/Asterisk.Platform.Api.Tests/ -v q
```

---

## Task 12: Full Build + Test Verification

**Phase:** C (Integration)

- [ ] **Step 1: Build entire solution**

```bash
dotnet build Asterisk.Platform.slnx
```

- [ ] **Step 2: Run entire test suite**

```bash
dotnet test Asterisk.Platform.slnx -v q
```

- [ ] **Step 3: Verify test count increased by ~25**

Expected: ~1,187 tests (was 1,162). New tests:
- 8 signature tests (Task 9)
- 5 event types tests (Task 9)
- 6 subscription store tests (Task 10)
- 8 delivery store tests (Task 10)
- 3 dispatcher tests (Task 11)
- 2 delivery service tests (Task 11)
- Total: ~32 new tests

---

## Task 13: Commit

- [ ] **Step 1: Stage and commit**

```bash
git add -A
git commit -m "feat(api): add outbound webhook subscriptions with HMAC-SHA256 delivery

- WebhookSubscription + WebhookDelivery domain models in Platform.Core
- WebhookSignatureService (HMAC-SHA256) + WebhookEventTypes registry (11 types)
- IWebhookSubscriptionStore + IWebhookDeliveryStore interfaces
- InMemory + Postgres store implementations (006_OutboundWebhooks.sql)
- WebhookDispatcher subscribes to PlatformEventBus, creates deliveries
- WebhookDeliveryService background worker with exponential backoff (8 attempts, ~24h)
- 13 endpoints: 8 tenant (subscription CRUD, test, deliveries, rotate-secret),
  2 management (dead-letter query + retry), 1 event-types listing
- ~32 new tests (signature, registry, stores, dispatcher, backoff)"
```

---

## Summary

| Task | Phase | Files | Tests |
|------|-------|-------|-------|
| 1: Domain Models + Interfaces | A | 7 new | 0 |
| 2: InMemory Storage | A | 2 new, 1 modified | 0 |
| 3: Postgres Storage | A | 3 new, 1 modified | 0 |
| 4: WebhookDispatcher | B | 1 new | 0 |
| 5: WebhookDeliveryService | B | 1 new | 0 |
| 6: Tenant Endpoints | B | 1 new | 0 |
| 7: Mgmt + EventType Endpoints | B | 2 new | 0 |
| 8: ApiJsonContext + Program.cs | C | 2 modified | 0 |
| 9: Tests — Models + Signature | C | 2 new | ~13 |
| 10: Tests — InMemory Stores | C | 2 new | ~14 |
| 11: Tests — Dispatcher + Service | C | 2 new | ~5 |
| 12: Full Verification | C | 0 | verify all |
| 13: Commit | — | 0 | 0 |
| **Total** | | **23 new, 4 modified** | **~32 new** |

Endpoint count: 43 → 46 (3 new endpoint groups: WebhookSubscriptionEndpoints, ManagementWebhookEndpoints, WebhookEventTypeEndpoints).

Test count: 1,162 → ~1,194.
