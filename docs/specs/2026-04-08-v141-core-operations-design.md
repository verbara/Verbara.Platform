# v1.4.1 "Core Operations" — Design Specification

**Date:** 2026-04-08
**Version:** 1.4.1
**Status:** Approved
**Predecessor:** v1.4.0 "Multi-Tenant Production"

## Executive Summary

v1.4.1 makes the contact center operationally functional. The admin/billing/partner layers (v1.1.0–v1.4.0) are production-ready, but the agent operational layer has critical gaps: no automatic conversation distribution (ACD), no real-time event wiring, no conversation timeouts, missing Postgres persistence for 2 billing stores, in-memory-only agent capacity, and broken email attachments.

**8 sub-projects, 4 execution phases, single release tag.**

---

## Architecture Decision: Dual ACD Model (C+D)

**Voice:** Asterisk `app_queue` handles voice ACD natively (0ms overhead, 20 years battle-tested).
**Digital:** Platform `QueueDistributionWorker` distributes chat/email/social conversations via push (offer/accept/reject with timeout).
**Coordination:** Unified `AgentCapacityTracker` in Postgres. AMI sync service bridges voice events with digital availability via `QueuePause`/`QueueAdd`/`QueueRemove`.

```
┌───────────────────────────────────────────┐
│           AgentCapacityTracker             │
│      (Single Source of Truth — Postgres)   │
└──────────┬───────────────────┬────────────┘
           │                   │
  ┌────────▼────────┐ ┌───────▼──────────┐
  │ AMI Sync Service │ │ QueueDistribution │
  │ (Voice events)   │ │ Worker (Digital)  │
  │                  │ │                   │
  │ AgentConnect →   │ │ Queued conv →     │
  │  +1 voice        │ │  select agent →   │
  │  QueuePause if   │ │  offer via SSE →  │
  │  capacity full   │ │  +1 chat          │
  │                  │ │  QueuePause if     │
  │ AgentComplete →  │ │  voice full       │
  │  -1 voice        │ │                   │
  │  QueueUnpause    │ │                   │
  └──────────────────┘ └───────────────────┘
           │                   │
  ┌────────▼────────┐ ┌───────▼──────────┐
  │  Asterisk 22     │ │  SSE / EventBus  │
  │  app_queue       │ │  (notify agent)  │
  └──────────────────┘ └───────────────────┘
```

---

## Sub-project A: Queue Distribution Worker

### Purpose

Background service that monitors queues for pending digital conversations and offers them to available agents using the push model (offer → accept/reject → timeout → re-offer).

### Components

#### 1. QueueDistributionWorker : BackgroundService

**Location:** `src/Asterisk.Platform.Api/Services/QueueDistributionWorker.cs`

**Dependencies:**
- `IConversationStore` — query queued conversations
- `IQueueStore` — load queue config
- `IAgentSelector` — select best agent (already implemented: `RoundRobinAgentSelector`)
- `IConversationSwitchboard` — offer/reject (already implemented)
- `IAgentCapacityService` — capacity check (already implemented)
- `PlatformEventBus` — publish offer events
- `IClock` — timestamps

**Loop (PeriodicTimer, 2-second interval):**
1. For each active tenant (from `ITenantStore.ListActiveAsync()`):
2. Query `IConversationStore.ListQueuedAsync(tenantId, limit: 50)` — new store method
3. Order by priority DESC, then CreatedAt ASC (FIFO within priority)
4. For each conversation:
   - Get queue via `IQueueStore.GetByIdAsync(tenantId, conversation.Owner.OwnerId)`
   - Call `IAgentSelector.SelectAgentAsync(tenantId, queueId, channel, preferredAgentId)`
   - If agent found: call `IConversationSwitchboard.OfferToAgentAsync()` + publish `ConversationOfferedEvent`
   - If no agent: skip (conversation remains queued for next cycle)

**Offer tracking:** Store `OfferedAt` and `OfferedToAgentId` in `conversation.Metadata["_offeredAt"]` and `conversation.Metadata["_offeredTo"]`. This avoids schema changes while enabling timeout detection.

#### 2. ConversationTimeoutWorker : BackgroundService

**Location:** `src/Asterisk.Platform.Api/Services/ConversationTimeoutWorker.cs`

**Dependencies:** `IConversationStore`, `IConversationSwitchboard`, `PlatformEventBus`, `IClock`

**Loop (PeriodicTimer, 5-second interval):**

For each active tenant (from `ITenantStore.ListActiveAsync()`):

**Phase 1 — Offer timeouts:**
1. Query `IConversationStore.ListByStateAsync(tenantId, ConversationState.Offered, limit: 100)` — new store method
2. For each: read `Metadata["_offeredAt"]`
3. If elapsed > `OfferTimeout` (default 30s from `InboundRoutingOptions`): call `IConversationSwitchboard.RejectAsync()` → returns to Queued
4. Publish `ConversationOfferExpiredEvent`

**Phase 2 — Queue abandonment:**
1. Query conversations in Queued state
2. If elapsed > `QueueTimeout` (default 300s, configurable per queue via `Queue.MaxWaitSeconds`): transition to `Abandoned`
3. Publish `ConversationAbandonedEvent`

**Phase 3 — WrapUp enforcement:**
1. Query conversations in WrapUp state
2. If elapsed > `WrapUpTimeout` (default 120s, configurable per queue via `Queue.WrapUp.MaxSeconds`): transition to `Closed`
3. Publish `ConversationAutoClosedEvent`

#### 3. Store Interface Extensions

**IConversationStore — new methods:**

```csharp
Task<IReadOnlyList<Conversation>> ListQueuedAsync(
    TenantId tenantId, int limit, CancellationToken ct);

Task<IReadOnlyList<Conversation>> ListByStateAsync(
    TenantId tenantId, ConversationState state, int limit, CancellationToken ct);
```

**InMemory implementation:** Filter `_store.Values.Where(c => c.State == state)` ordered by `CreatedAt`.

**Postgres implementation:** `SELECT ... FROM conversations WHERE tenant_id = @tid AND state = @state ORDER BY created_at LIMIT @limit`.

### Configuration

```csharp
public sealed class DistributionOptions
{
    public int PollIntervalMs { get; set; } = 2000;
    public int OfferTimeoutSeconds { get; set; } = 30;
    public int DefaultQueueTimeoutSeconds { get; set; } = 300;
    public int DefaultWrapUpTimeoutSeconds { get; set; } = 120;
    public int MaxConversationsPerCycle { get; set; } = 50;
}
```

Bound from `Distribution` config section in `Program.cs`.

### Tests

- `QueueDistributionWorkerTests` — offer to available agent, skip when no agents, respect capacity, round-robin selection
- `ConversationTimeoutWorkerTests` — offer timeout returns to queue, queue timeout transitions to Abandoned, wrap-up timeout closes

**Estimated new tests:** ~20

---

## Sub-project B: Asterisk Capacity Sync

### Purpose

AMI listener that synchronizes voice call events with the unified capacity tracker and pauses/unpauses agents in Asterisk queues based on digital capacity changes.

### Components

#### 1. AsteriskCapacitySyncService : BackgroundService

**Location:** `src/Asterisk.Platform.Api/Services/AsteriskCapacitySyncService.cs`

**Dependencies:**
- `IAmiClient` (from Asterisk.Sdk) — AMI event subscription + actions
- `IAgentCapacityService` — update capacity on voice events
- `IAgentStore` — resolve agent by extension
- `IQueueMembershipStore` — get agent's queue memberships
- `PlatformEventBus` — subscribe to digital capacity changes

**AMI Event Handlers:**

| AMI Event | Action |
|-----------|--------|
| `QueueMemberStatus` (status=InUse) | `IAgentCapacityService.ReserveAsync(agentId, Voice)` |
| `QueueMemberStatus` (status=NotInUse) | `IAgentCapacityService.ReleaseAsync(agentId, Voice)` |
| `AgentConnect` | Confirm voice reservation, log |
| `AgentComplete` | Release voice capacity, check if should unpause |

**Agent Resolution:** Extract tenant + extension from AMI interface name `PJSIP/{tenantId}_ext{extension}`. Lookup agent via `IAgentStore.GetByExtensionAsync(tenantId, extension)` — new store method.

**IAgentStore — new method:**
```csharp
Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct);
```

#### 2. Capacity-to-AMI Sync (Digital → Voice)

Subscribe to `PlatformEventBus` for `AgentCapacityChangedEvent`:
- When digital capacity change makes `CanAcceptVoice == false`: send `QueuePause` AMI action
- When digital capacity freed and `CanAcceptVoice == true`: send `QueuePause(paused=false)` AMI action
- Scope: all queues the agent is member of (via `IQueueMembershipStore`)

#### 3. AgentCapacityChangedEvent

**New event:** Published by `IAgentCapacityService` when capacity changes.

```csharp
public sealed record AgentCapacityChangedEvent(
    string TenantId, string AgentId, string Channel,
    int CurrentLoad, int MaxLoad, bool CanAcceptVoice)
    : PlatformEvent(TenantId, "agent.capacity_changed", DateTimeOffset.UtcNow);
```

### Conditional Wiring

Only registered when Asterisk AMI is configured:
```csharp
if (amiClient is not null)
    builder.Services.AddHostedService<AsteriskCapacitySyncService>();
```

### Tests

- `AsteriskCapacitySyncServiceTests` — voice event updates capacity, digital capacity change pauses agent in AMI, unpause when freed
- **Estimated new tests:** ~12

---

## Sub-project C: Missing Postgres Stores

### Purpose

Implement PostgresDunningStore and PostgresTenantAddOnStore to prevent data loss on restart.

### Components

#### 1. Migration 013_DunningAddOns.sql

```sql
CREATE TABLE IF NOT EXISTS dunning_records (
    dunning_id      VARCHAR(64) PRIMARY KEY,
    tenant_id       VARCHAR(64) NOT NULL,
    invoice_id      VARCHAR(64) NOT NULL,
    current_stage   VARCHAR(32) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL,
    escalated_at    TIMESTAMPTZ,
    resolved_at     TIMESTAMPTZ,
    is_paused       BOOLEAN NOT NULL DEFAULT FALSE,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_dunning_records_tenant ON dunning_records(tenant_id) WHERE is_active = TRUE;
CREATE INDEX idx_dunning_records_invoice ON dunning_records(invoice_id) WHERE is_active = TRUE;

CREATE TABLE IF NOT EXISTS tenant_add_ons (
    tenant_id       VARCHAR(64) NOT NULL,
    feature         VARCHAR(64) NOT NULL,
    enabled_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, feature)
);

CREATE INDEX idx_tenant_add_ons_tenant ON tenant_add_ons(tenant_id);
```

#### 2. PostgresDunningStore

**Location:** `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresDunningStore.cs`

Implements `IDunningStore` with Dapper. Row type: `DunningRecordRow` (class-based `{get; init;}`).

| Method | SQL |
|--------|-----|
| `GetActiveAsync(tenantId)` | `SELECT ... WHERE tenant_id = @tid AND is_active = TRUE LIMIT 1` |
| `GetByInvoiceAsync(invoiceId)` | `SELECT ... WHERE invoice_id = @iid AND is_active = TRUE LIMIT 1` |
| `ListActiveAsync()` | `SELECT ... WHERE is_active = TRUE` |
| `UpsertAsync(record)` | `INSERT ... ON CONFLICT (dunning_id) DO UPDATE SET ...` |

#### 3. PostgresTenantAddOnStore

**Location:** `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAddOnStore.cs`

Implements `ITenantAddOnStore` with Dapper. Row type: `TenantAddOnRow` (class-based `{get; init;}`).

| Method | SQL |
|--------|-----|
| `GetAsync(tenantId)` | `SELECT ... WHERE tenant_id = @tid` |
| `UpsertAsync(addOn)` | `INSERT ... ON CONFLICT (tenant_id, feature) DO UPDATE SET enabled_at = @enabledAt` |
| `DeleteAsync(tenantId, feature)` | `DELETE FROM tenant_add_ons WHERE tenant_id = @tid AND feature = @f` |

#### 4. DI Registration

Add both stores to `PostgresStorageExtensions.AddPostgresStorage()`.

### Tests

- `PostgresDunningStoreTests` — CRUD, active filtering, upsert idempotency
- `PostgresTenantAddOnStoreTests` — CRUD, delete, list by tenant
- **Estimated new tests:** ~10

---

## Sub-project D: Agent Capacity Persistence

### Purpose

Persist agent capacity state to survive restarts and enable reconciliation.

### Components

#### 1. IAgentCapacityStore (new interface)

**Location:** `src/Asterisk.Platform.Queues/Services/IAgentCapacityStore.cs`

```csharp
public interface IAgentCapacityStore
{
    Task<IReadOnlyList<AgentCapacityRecord>> ListByTenantAsync(
        TenantId tenantId, CancellationToken ct);
    Task UpsertAsync(AgentCapacityRecord record, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task ClearAllAsync(CancellationToken ct);
}

public sealed class AgentCapacityRecord
{
    public required string TenantId { get; init; }
    public required string AgentId { get; init; }
    public int VoiceLoad { get; set; }
    public int ChatLoad { get; set; }
    public int EmailLoad { get; set; }
    public int SmsLoad { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

#### 2. PersistentAgentCapacityService

**Location:** `src/Asterisk.Platform.Queues/Services/PersistentAgentCapacityService.cs`

Wraps `InMemoryAgentCapacityService` + writes through to `IAgentCapacityStore` on every Reserve/Release. On startup, reconciles from store.

**Startup reconciliation:**
1. Load all `AgentCapacityRecord` from store
2. Cross-reference with active conversations (`IConversationStore.ListByStateAsync(Active)`)
3. Rebuild in-memory capacity from actual conversation ownership
4. Update store with reconciled values

This ensures that after a restart, capacity matches reality (active conversations assigned to agents).

#### 3. Migration (in 013_DunningAddOns.sql)

```sql
CREATE TABLE IF NOT EXISTS agent_capacity (
    tenant_id   VARCHAR(64) NOT NULL,
    agent_id    VARCHAR(64) NOT NULL,
    voice_load  INTEGER NOT NULL DEFAULT 0,
    chat_load   INTEGER NOT NULL DEFAULT 0,
    email_load  INTEGER NOT NULL DEFAULT 0,
    sms_load    INTEGER NOT NULL DEFAULT 0,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, agent_id)
);
```

#### 4. InMemory + Postgres implementations of IAgentCapacityStore

Standard pattern matching existing stores.

### Tests

- `PersistentAgentCapacityServiceTests` — reserve/release persists, startup reconciliation rebuilds from conversations
- **Estimated new tests:** ~10

---

## Sub-project E: Email Attachment Fix

### Purpose

Fix `EmailConnector.AddUrlAttachment()` to download the actual resource instead of storing the URL string as attachment content.

### Current Broken Code

```csharp
// src/Asterisk.Platform.Channels.Email/EmailConnector.cs:140-145
private static void AddUrlAttachment(MailMessage mail, string url, string fileName, string mimeType)
{
    var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(url)); // BUG: stores URL text
    mail.Attachments.Add(new Attachment(data, fileName, mimeType));
}
```

### Fix

Inject `IHttpClientFactory` into `EmailConnector`. Download the resource via `HttpClient.GetByteArrayAsync(url)` with:
- 30-second timeout
- 25MB max download size (match Platform.Web limit)
- Content-Type validation against `mimeType`
- Fallback: if download fails, skip attachment and log warning (don't fail the entire send)

```csharp
private async Task AddUrlAttachmentAsync(
    MailMessage mail, string url, string fileName, string mimeType, CancellationToken ct)
{
    try
    {
        var client = _httpClientFactory.CreateClient("EmailAttachments");
        var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > MaxAttachmentBytes)
        {
            _logger.LogWarning("Attachment {Url} exceeds max size ({Size} bytes), skipping", url, bytes.Length);
            return;
        }

        var data = new MemoryStream(bytes);
        mail.Attachments.Add(new Attachment(data, fileName, mimeType));
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to download attachment {Url}, skipping", url);
    }
}
```

Register named HttpClient:
```csharp
builder.Services.AddHttpClient("EmailAttachments", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 25 * 1024 * 1024;
});
```

### Tests

- `EmailConnectorTests` — successful download attaches binary, failed download skips gracefully, oversized attachment skipped
- **Estimated new tests:** ~5

---

## Sub-project F: SSE Event Wiring

### Purpose

Wire PlatformEventBus publishing into all core operational flows so the Agent Workspace receives real-time updates.

### Current State

- `PlatformEventBus` exists (Rx-based Subject)
- `SseEndpoints.cs` subscribes and streams to clients
- Agent Workspace (Platform.Web) connects to SSE and reacts to events
- **Problem:** Core flows don't publish events

### Events to Wire

| Flow | File | Event to Publish |
|------|------|-----------------|
| Inbound message received | `WebhookEndpoints.cs:83` (after pipeline) | `ConversationMessageEvent` (new inbound) |
| Conversation created | `WebhookEndpoints.cs:83` (if `IsNewConversation`) | `ConversationCreatedEvent` |
| Conversation assigned to queue | `ConversationSwitchboard.cs:38-42` (AssignToQueueAsync) | `ConversationStateChangedEvent(Queued)` |
| Conversation offered to agent | `ConversationSwitchboard.cs:59-62` (OfferToAgentAsync) | `ConversationOfferedEvent` (new event) |
| Conversation accepted | `ConversationSwitchboard.cs:80-88` (AcceptAsync) | `ConversationAssignedEvent` + `ConversationStateChangedEvent(Active)` |
| Conversation rejected | `ConversationSwitchboard.cs:105-110` (RejectAsync) | `ConversationStateChangedEvent(Queued)` |
| Agent sends message | `DefaultConversationService.cs:78` (after send) | `ConversationMessageEvent` (outbound) |
| Conversation transferred | `ConversationSwitchboard.cs` (TransferTo*) | `ConversationStateChangedEvent` |
| Conversation closed | `ConversationSwitchboard.cs` or endpoint | `ConversationClosedEvent` |
| Agent state changed | `AgentEndpoints.cs` (PUT /agents/me/state) | `AgentStateChangedEvent` (verify already published) |

### New Events

```csharp
public sealed record ConversationOfferedEvent(
    string TenantId, string ConversationId, string AgentId, string QueueId)
    : PlatformEvent(TenantId, "conversation.offered", DateTimeOffset.UtcNow);

public sealed record ConversationMessageEvent(
    string TenantId, string ConversationId, string MessageId,
    string Direction, string Channel)
    : PlatformEvent(TenantId, "conversation.message", DateTimeOffset.UtcNow);
```

### Implementation Approach

Inject `PlatformEventBus` into `ConversationSwitchboard` constructor (add parameter). Publish after each successful state transition. For `WebhookEndpoints` and `DefaultConversationService`, publish after persistence succeeds.

**Thread safety:** `PlatformEventBus.Publish()` is already thread-safe (Rx Subject with synchronization).

### Tests

- Verify events are published for each flow
- **Estimated new tests:** ~12

---

## Sub-project G: Conversation Lifecycle Timeouts

### Purpose

Implement automatic state transitions for stale conversations (covered by ConversationTimeoutWorker in Sub-project A).

### Covered By Sub-project A

The `ConversationTimeoutWorker` handles all three timeout scenarios:
1. **Offer timeout** (30s default) — Offered → Queued (re-offer to next agent)
2. **Queue timeout** (300s default) — Queued → Abandoned
3. **WrapUp timeout** (120s default) — WrapUp → Closed

### Additional: Queue.MaxWaitSeconds

Add `MaxWaitSeconds` property to `Queue` model (nullable int, default null = use global default).

**Location:** `src/Asterisk.Platform.Queues/Queue.cs`

```csharp
public int? MaxWaitSeconds { get; set; }
```

No migration needed — already stored in queue_configs table as nullable column, or can use existing `timeout` column from Asterisk Realtime schema.

### Additional: WrapUp.MaxSeconds

Already exists in `WrapUpConfig` model but is not enforced. The `ConversationTimeoutWorker` will read this value.

### Tests

Covered by Sub-project A tests.

---

## Sub-project H: Agent State in Workspace

### Purpose

Add agent availability state toggle to the Agent Workspace in Platform.Web.

### Scope

**Backend:** Verify `PUT /api/v1/agents/me/state` works correctly. Already exists in `AgentEndpoints.cs`. No backend changes needed — the endpoint accepts `{ "state": "Available" }` and validates the state machine transition.

**Frontend (Platform.Web):** Add state toggle component to agent workspace header.

**Location:** `src/pages/agent/components/agent-status-selector.tsx` (new component)

**Behavior:**
- Dropdown/badge in workspace header showing current state (Available, Break, Lunch, etc.)
- Color-coded: green (Available), yellow (Break/Lunch/Training), red (DND/Offline)
- On change: `PUT /api/v1/agents/me/state` mutation
- SSE subscription to `agent.state_changed` for real-time sync
- Disable state changes while in active voice call

**Hook:** Add `useAgentState` mutation to existing `use-agents.ts` hooks file.

### Tests

- Agent state toggle E2E test (if Playwright agent workspace tests exist)
- **Estimated new tests:** ~3 (frontend)

---

## Execution Phases

### Phase 1: Foundation (F + H) — Unlocks UI Testing

| Task | Package | Effort |
|------|---------|--------|
| Wire SSE events into Switchboard | Platform.Switchboard | 1 day |
| Wire SSE events into WebhookEndpoints | Platform.Api | 0.5 day |
| Wire SSE events into ConversationService | Platform.Api | 0.5 day |
| Agent state selector component | Platform.Web | 0.5 day |

**Verification:** Agent logs in → changes state to Available → receives SSE events when messages arrive.

### Phase 2: ACD (A + G) — The Core

| Task | Package | Effort |
|------|---------|--------|
| IConversationStore.ListQueuedAsync/ListByStateAsync | Platform.Conversations + both Storage | 1 day |
| QueueDistributionWorker | Platform.Api | 2 days |
| ConversationTimeoutWorker | Platform.Api | 1 day |
| DistributionOptions config | Platform.Api | 0.5 day |

**Verification:** WhatsApp message arrives → routed to queue → worker offers to agent → agent accepts in UI → agent replies → WhatsApp receives reply.

### Phase 3: Persistence (C + D) — Survive Restarts

| Task | Package | Effort |
|------|---------|--------|
| Migration 013 (dunning + addons + capacity) | Platform.Storage.Postgres | 0.5 day |
| PostgresDunningStore | Platform.Storage.Postgres | 0.5 day |
| PostgresTenantAddOnStore | Platform.Storage.Postgres | 0.5 day |
| IAgentCapacityStore + implementations | Platform.Queues + both Storage | 1 day |
| PersistentAgentCapacityService + reconciliation | Platform.Queues | 1 day |

**Verification:** Restart server → capacity state survives → dunning records persist → add-ons persist.

### Phase 4: Integration (B + E) — Voice Bridge + Fix

| Task | Package | Effort |
|------|---------|--------|
| AsteriskCapacitySyncService | Platform.Api | 2 days |
| IAgentStore.GetByExtensionAsync | Platform.Queues + both Storage | 0.5 day |
| Email attachment download fix | Platform.Channels.Email | 0.5 day |
| HttpClient registration | Platform.Api | 0.25 day |

**Verification:** Agent takes voice call → digital capacity updated → agent takes chat → Asterisk queue paused → email with attachment arrives correctly.

---

## Test Summary

| Sub-project | New Tests | Package |
|-------------|-----------|---------|
| A: QueueDistributionWorker | ~20 | Platform.Api.Tests |
| B: AsteriskCapacitySyncService | ~12 | Platform.Api.Tests |
| C: Postgres Stores | ~10 | Platform.Storage.Postgres.Tests |
| D: Capacity Persistence | ~10 | Platform.Queues.Tests |
| E: Email Fix | ~5 | Platform.Channels.Email.Tests |
| F: SSE Wiring | ~12 | Platform.Api.Tests + Platform.Switchboard.Tests |
| G: Timeouts | (included in A) | — |
| H: Agent State UI | ~3 | Platform.Web (E2E) |
| **Total** | **~72** | |

---

## Files Modified (Estimated)

### New Files (~15)
- `src/Asterisk.Platform.Api/Services/QueueDistributionWorker.cs`
- `src/Asterisk.Platform.Api/Services/ConversationTimeoutWorker.cs`
- `src/Asterisk.Platform.Api/Services/AsteriskCapacitySyncService.cs`
- `src/Asterisk.Platform.Api/Services/DistributionOptions.cs`
- `src/Asterisk.Platform.Queues/Services/IAgentCapacityStore.cs`
- `src/Asterisk.Platform.Queues/Services/AgentCapacityRecord.cs`
- `src/Asterisk.Platform.Queues/Services/PersistentAgentCapacityService.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryAgentCapacityStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAgentCapacityStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresDunningStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAddOnStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Migrations/013_DunningAddOnsCapacity.sql`
- Event records (2-3 new files in Platform.Core/Events or Platform.Conversations/Events)

### Modified Files (~15)
- `src/Asterisk.Platform.Conversations/IConversationStore.cs` — 2 new methods
- `src/Asterisk.Platform.Storage.InMemory/InMemoryConversationStore.cs` — implement new methods
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresConversationStore.cs` — implement new methods
- `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs` — inject + publish EventBus
- `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs` — publish events after pipeline
- `src/Asterisk.Platform.Api/Services/DefaultConversationService.cs` — publish message events
- `src/Asterisk.Platform.Queues/IAgentStore.cs` — GetByExtensionAsync
- `src/Asterisk.Platform.Queues/Services/InMemoryAgentCapacityService.cs` — publish capacity events
- `src/Asterisk.Platform.Channels.Email/EmailConnector.cs` — download attachments
- `src/Asterisk.Platform.Api/Program.cs` — register new services
- `src/Asterisk.Platform.Storage.InMemory/InMemoryStorageExtensions.cs` — register new stores
- `src/Asterisk.Platform.Storage.Postgres/PostgresStorageExtensions.cs` — register new stores
- `src/Asterisk.Platform.Api/ApiJsonContext.cs` — register new DTOs for AOT

---

## Non-Goals (Deferred to v1.5.0+)

- SMS provider (Twilio ISmsProvider)
- WebChat transport (IWebChatTransport)
- Analytics channel type tracking
- Report templates (BuildReportData)
- Plan auto-cascade
- Partner plan catalog
- Add-on quotas
- Notification preferences
- Branding fonts + login BG
- Bot intercept routing fix (separate concern)
- SignalR replacement for SSE
