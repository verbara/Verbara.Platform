# v1.5.0 "Production Ready" Design Spec

**Goal:** Make Asterisk.Platform sellable to Partners by fixing critical bugs, completing the agent workspace, enabling WebChat demos, implementing real report templates, hardening for production deployment, and expanding with Twilio SMS + Cases API.

**Version:** 1.5.0
**Date:** 2026-04-09
**Depends on:** v1.4.1 (Core Operations) complete

---

## Context

v1.4.1 delivered ACD distribution, SSE event wiring, conversation timeouts, and agent capacity persistence. The administrative shell (tenants, billing, partner portal, RBAC, branding, notifications) is excellent. However, deep analysis revealed:

1. **Broken features:** Bot handoff never executes (`TransferToQueue` ignored in WebhookEndpoints), no hold/unhold API endpoints despite state machine support, no outbound conversation creation endpoint
2. **Demo blockers:** WebChat has no transport implementation (only interface stub) and no customer widget. It's the only channel demostrable without third-party setup
3. **Agent gaps:** No canned responses backend (frontend has hardcoded mock data), supervisor cannot see digital conversations (only voice sessions)
4. **Report stubs:** `BuildReportData()` returns empty data. No real report templates exist
5. **Production blockers:** Zero health checks, no migration runner, hardcoded secrets (`admin/admin`, `platform_internal_secret`), CORS defaults to `*`
6. **Missing integrations:** SMS has no provider (ISmsProvider interface only), Cases feature has store + model but zero API endpoints

## Architecture

6 sub-projects across 4 phases:

```
Phase 1: Sub-project A (Critical Fixes)           -- no dependencies, unblocks everything
Phase 2: Sub-project B (WebChat) + C (Agent)       -- parallel, demo-critical
Phase 3: Sub-project D (Reports) + E (Hardening)   -- parallel
Phase 4: Sub-project F (Twilio SMS + Cases)         -- market expansion
```

All sub-projects are independent and can be developed in isolation. Phase ordering reflects logical priority, not technical dependency.

**New packages:** None (all work in existing packages)
**New migrations:** 014 (CannedResponses + BotAnalytics + Cases columns if needed)
**New files:** ~25 across Platform + Platform.Web
**Estimated tests:** ~80-100 new tests

---

## Sub-project A: Critical Fixes

Fixes broken features and fundamental gaps that prevent the product from functioning correctly.

### A.1: Bot Handoff Execution

**Problem:** In `WebhookEndpoints.cs`, when `BotOrchestrator.ProcessMessageAsync()` returns `BotResponseAction.TransferToQueue` or `BotResponseAction.EndConversation`, the code is ignored. Only `Reply` is handled. Conversations remain stuck with the bot forever after handoff.

**Fix:** After the existing `Reply` handler block (~line 116), add:

```csharp
if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
{
    // Clear bot ownership and assign to target queue
    await switchboard.AssignToQueueAsync(
        tid,
        conversation.ConversationId,
        botResponse.TargetQueueId.Value,
        botResponse.Priority ?? MessagePriority.Normal,
        ct);

    eventBus.Publish(new ConversationStateChangedEvent(
        tid.Value, conversation.ConversationId.Value, ConversationState.Queued.ToString()));
}
else if (botResponse.Action == BotResponseAction.EndConversation)
{
    await lifecycleService.CloseAsync(tid, conversation.ConversationId, wrapUp: null, ct);
}
```

**Files:** `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs`
**Tests:** 3 tests — handoff routes to queue, end closes conversation, reply still works

### A.2: Hold/Unhold Endpoints

**Problem:** `ConversationState.OnHold` (11) and transitions Active ↔ OnHold exist in the state machine, but no API endpoint exposes them.

**Fix:** Add to `ConversationEndpoints.cs`:

- `POST /conversations/{id}/hold` — validates agent owns conversation, transitions Active → OnHold via state machine, publishes `ConversationStateChangedEvent`
- `POST /conversations/{id}/unhold` — validates agent owns conversation, transitions OnHold → Active, publishes `ConversationStateChangedEvent`

**Switchboard changes:** Add `HoldAsync(tenantId, conversationId, agentId, ct)` and `UnholdAsync(tenantId, conversationId, agentId, ct)` methods to `IConversationSwitchboard` and `ConversationSwitchboard`. Both validate ownership, call state machine transition, save, and publish events.

**Files:**
- `src/Asterisk.Platform.Switchboard/IConversationSwitchboard.cs` — add 2 methods
- `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs` — implement
- `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs` — add 2 routes

**Tests:** 4 tests — hold succeeds, unhold succeeds, hold from wrong state fails, non-owner cannot hold

### A.3: Outbound Conversation Creation

**Problem:** No `POST /conversations` endpoint. Agents cannot initiate outbound conversations with contacts. `GetOrCreateForContactAsync()` exists internally but is not exposed via API.

**Fix:** Add to `ConversationEndpoints.cs`:

```csharp
// POST /conversations
// Body: { "contactId": "xxx", "channel": "WhatsApp", "initialMessage": "Hello" (optional) }
```

- Calls `IConversationService.GetOrCreateForContactAsync(tenantId, contactId, channel, ct)`
- If `initialMessage` provided, sends it via `SendMessageAsync` with agent as sender
- Returns 201 Created with the conversation object

**Request DTO:** `CreateConversationRequest(string ContactId, string Channel, string? InitialMessage)`

**Files:** `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs`
**Tests:** 3 tests — creates conversation, creates with initial message, reuses existing conversation

### A.4: Error Handling Expansion

**Problem:** `ErrorHandlingMiddleware` only maps 3 exception types. `PlatformException` (which has a `Code` property) falls through to 500. `ArgumentException`, `OperationCanceledException` all become 500.

**Fix:** Expand the switch expression:

```csharp
var (status, title) = exception switch
{
    PlatformException px => (StatusCodes.Status400BadRequest, px.Code),
    ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
    InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
    UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
    KeyNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
    OperationCanceledException => (499, "Client Closed Request"),
    _ => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
};
```

Add to ProblemDetails response:
- `Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier`
- Log tenant ID from `context.Items["TenantId"]` when available

Convert `#pragma disable CA1848` to proper `[LoggerMessage]` delegates.

**Files:** `src/Asterisk.Platform.Api/Middleware/ErrorHandlingMiddleware.cs`
**Tests:** 5 tests — PlatformException maps to 400 with code, ArgumentException maps to 400, OperationCanceledException maps to 499, traceId present, unknown exception maps to 500

---

## Sub-project B: WebChat End-to-End

Makes WebChat the showcase channel for demos — no third-party accounts needed.

### B.1: WebSocket Transport

**Implementation:** `WebSocketWebChatTransport` implements `IWebChatTransport`.

```csharp
public sealed class WebSocketWebChatTransport : IWebChatTransport
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public async Task SendToClientAsync(string sessionId, MessageEnvelope message, CancellationToken ct)
    {
        if (!_connections.TryGetValue(sessionId, out var ws) || ws.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.SerializeToUtf8Bytes(
            new WebChatWsMessage("message", message),
            WebChatJsonContext.Default.WebChatWsMessage);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

    public async Task DisconnectAsync(string sessionId, CancellationToken ct)
    {
        if (_connections.TryRemove(sessionId, out var ws) && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", ct);
    }

    // Called by WebSocket middleware to register connection
    public void Register(string sessionId, WebSocket ws) => _connections[sessionId] = ws;
    public void Unregister(string sessionId) => _connections.TryRemove(sessionId, out _);
}
```

**WebSocket message protocol:**
```json
// Server → Client
{ "type": "message", "data": { "blocks": [...] } }
{ "type": "typing", "data": { "agentName": "Maria" } }
{ "type": "connected", "data": { "sessionId": "xxx" } }
{ "type": "ended", "data": { "reason": "agent_closed" } }

// Client → Server
{ "type": "message", "data": { "text": "Hello" } }
{ "type": "typing", "data": {} }
```

**AOT serialization:** `WebChatJsonContext` with `[JsonSerializable]` for all WebSocket message types.

**Files:**
- `src/Asterisk.Platform.Channels.WebChat/WebSocketWebChatTransport.cs` — new
- `src/Asterisk.Platform.Channels.WebChat/WebChatWsMessage.cs` — new (message envelope)
- `src/Asterisk.Platform.Channels.WebChat/WebChatJsonContext.cs` — new (AOT context)
- `src/Asterisk.Platform.Channels.WebChat/ServiceCollectionExtensions.cs` — register transport

**Tests:** 4 tests — send to connected client, send to disconnected is no-op, disconnect closes socket, register/unregister lifecycle

### B.2: WebChat HTTP Endpoints

**New file:** `WebChatEndpoints.cs` in Platform.Api/Endpoints.

**Endpoints:**

- `POST /webchat/sessions` (AllowAnonymous) — creates session via `WebChatSessionManager.ConnectAsync()`, returns `{ sessionId, wsUrl: "/ws/webchat/{sessionId}" }`. Requires `tenantId` in body (validated against `ITenantStore`).

- `GET /ws/webchat/{sessionId}` (WebSocket upgrade) — ASP.NET WebSocket middleware. Accepts connection, registers in transport, starts read loop:
  - On `"message"` type: creates `InboundMessage`, processes through `IInboundMessagePipeline`
  - On `"typing"` type: publishes typing event via `PlatformEventBus`
  - On close: calls `WebChatSessionManager.DisconnectAsync()`

- `POST /webchat/sessions/{sessionId}/messages` (AllowAnonymous) — REST fallback for environments where WebSocket is blocked. Accepts `{ text }`, processes same as WebSocket message path.

**Inbound flow:** WebSocket message → parse → `InboundMessage` construction → `IInboundMessagePipeline.ProcessAsync()` → same routing as all other channels.

**Files:**
- `src/Asterisk.Platform.Api/Endpoints/WebChatEndpoints.cs` — new
- `src/Asterisk.Platform.Api/Program.cs` — map WebChat endpoints + WebSocket middleware

**Tests:** 5 tests — session creation, message through pipeline, REST fallback, invalid session rejected, tenant validation

### B.3: Customer WebChat Widget

**Location:** Platform.Web, standalone embeddable component.

**Implementation:** A self-contained JavaScript file (`webchat-widget.js`) that customers embed:

```html
<script src="https://platform.example.com/webchat/widget.js"
        data-tenant="tenant-id"
        data-position="bottom-right"
        data-title="Chat with us">
</script>
```

**Widget features:**
- Floating chat bubble (bottom-right by default, configurable)
- On click: opens chat panel with message thread
- Connects via WebSocket to `/ws/webchat/{sessionId}`
- Fetches tenant branding via `GET /branding/{tenantId}` (already exists, public) for colors/logo
- Message input with send button
- Typing indicators (sends and receives)
- File upload (drag & drop, max 25MB, uploads via REST `POST /webchat/sessions/{id}/messages` with multipart form-data since WebSocket is text-only; widget switches to REST for file sends)
- Session persistence via `localStorage` (reconnect on page reload)
- Responsive: adapts to mobile
- No framework dependency (vanilla JS + CSS, <50KB gzipped)

**Served by Platform.Api:** Static file middleware serves `webchat-widget.js` from `/webchat/widget.js` path.

**Files:**
- `src/Asterisk.Platform.Api/wwwroot/webchat/widget.js` — new
- `src/Asterisk.Platform.Api/wwwroot/webchat/widget.css` — new
- `src/Asterisk.Platform.Api/Program.cs` — add `UseStaticFiles()` for wwwroot

**Tests:** Manual testing + 3 E2E tests in Platform.Web (widget loads, sends message, receives reply)

### B.4: Branding Integration

The widget already consumes `GET /branding/{tenantId}` which returns `TenantBranding` with `PrimaryColor`, `LogoUrl`, `DisplayName`, etc. No backend changes needed — widget JS fetches and applies CSS variables.

---

## Sub-project C: Agent Workspace Completion

Completes agent tools and gives supervisors visibility over digital conversations.

### C.1: Canned Responses Backend

**Model:**

```csharp
public sealed class CannedResponse
{
    public required EntityId ResponseId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Shortcut { get; set; }     // e.g. "/greeting", "/closing"
    public required string Title { get; set; }         // Display name
    public required string Body { get; set; }          // Template with {{contact.name}} vars
    public string? Category { get; set; }              // "Greetings", "Closings", "FAQ"
    public IReadOnlyList<string> Tags { get; set; } = [];
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

**Store interface:**

```csharp
public interface ICannedResponseStore
{
    Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct);
    Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct);
    Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct);
    Task SaveAsync(CannedResponse response, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct);
}
```

**Search logic:** Matches `query` against Shortcut, Title, Body, Category, and Tags (case-insensitive contains). Postgres: `ILIKE` or `to_tsvector`/`to_tsquery` for full-text.

**API:**
- Admin CRUD: `GET/POST/PUT/DELETE /admin/canned-responses` (AdminOnly)
- Agent search: `GET /canned-responses?q={query}` (Authenticated) — returns matching responses for the `/` trigger in reply composer

**Migration 014:** Table `canned_responses`:
```sql
CREATE TABLE IF NOT EXISTS canned_responses (
    response_id TEXT NOT NULL,
    tenant_id   TEXT NOT NULL,
    shortcut    TEXT NOT NULL,
    title       TEXT NOT NULL,
    body        TEXT NOT NULL,
    category    TEXT,
    tags        TEXT, -- JSON array
    created_by  TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, response_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canned_responses_shortcut
    ON canned_responses (tenant_id, shortcut);
```

**Frontend (Platform.Web):** Replace hardcoded array in `canned-responses.tsx` with `useCannedResponses(query)` hook that calls `GET /canned-responses?q={query}`.

**Files:**
- `src/Asterisk.Platform.Conversations/CannedResponse.cs` — new model
- `src/Asterisk.Platform.Conversations/ICannedResponseStore.cs` — new interface
- `src/Asterisk.Platform.Storage.InMemory/InMemoryCannedResponseStore.cs` — new
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresCannedResponseStore.cs` — new
- `src/Asterisk.Platform.Storage.Postgres/Migrations/014_CannedResponsesBotAnalytics.sql` — new
- `src/Asterisk.Platform.Api/Endpoints/CannedResponseEndpoints.cs` — new (admin + agent)
- Platform.Web: `src/core/api/hooks/use-canned-responses.ts` — new hook
- Platform.Web: `src/agent/conversation/canned-responses.tsx` — update to use hook

**Tests:** 6 tests — CRUD operations, search by shortcut, search by category, agent search endpoint, duplicate shortcut rejected, tenant isolation

### C.2: Supervisor Digital Conversation Monitoring

**Problem:** `SupervisorEndpoints.cs` only has 3 methods for voice sessions via `AgentAssistSupervisor`. Supervisors have zero visibility into digital conversations (WhatsApp, Email, WebChat, etc.).

**New endpoints in SupervisorEndpoints.cs:**

```csharp
// List active digital conversations (all non-terminal states)
GET /supervisor/conversations?queue={queueId}&agent={agentId}&channel={channel}&state={state}

// View conversation messages (read-only)
GET /supervisor/conversations/{id}/messages

// Supervisor takes ownership of conversation
POST /supervisor/conversations/{id}/takeover

// Force close conversation
POST /supervisor/conversations/{id}/close
// Body: { "reason": "Supervisor closed — policy violation" }

// Inject coaching note visible only to agent (digital whisper)
POST /supervisor/conversations/{id}/note
// Body: { "text": "Ask about their account number" }
```

**Takeover flow:**
1. Validate supervisor has `SupervisorPlus` auth
2. Release capacity from current agent (if any)
3. Transfer ownership to supervisor via `ConversationSwitchboard.TransferToAgentAsync()`
4. Publish `ConversationAssignedEvent`

**Coaching note:** Creates a `Message` with `SenderKind = System` and metadata `{ "visibility": "agent_only", "noteType": "supervisor_coaching" }`. Delivered via SSE to the agent but NOT sent to the customer's channel.

**Auth:** All endpoints require `SupervisorPlus` authorization policy.

**Files:**
- `src/Asterisk.Platform.Api/Endpoints/SupervisorEndpoints.cs` — add 5 new endpoints

**Tests:** 6 tests — list conversations filtered, view messages, takeover transfers ownership, force close works, coaching note not sent to customer, auth enforced

### C.3: Frontend Supervisor Digital

**Location:** Platform.Web

**Updates to monitor page or new digital-monitor tab:**
- Hook: `useSupervisorConversations(filters)` — `GET /supervisor/conversations` with 5s refetch
- Conversation list: agent name, channel icon, queue, duration, last message preview, state badge
- Click → message thread view (read-only)
- Action buttons: Takeover, Force Close, Send Note (dialog with textarea)
- Hook: `useTakeoverConversation()`, `useForceCloseConversation()`, `useSendSupervisorNote()`

**Files:**
- Platform.Web: `src/core/api/hooks/use-supervisor.ts` — add 4 new hooks
- Platform.Web: `src/operations/monitor/digital-conversations.tsx` — new component
- Platform.Web: `src/operations/monitor/monitor-page.tsx` — add tab for digital conversations

**Tests:** 3 E2E tests — list shows conversations, takeover works, note delivers to agent

---

## Sub-project D: Report Templates

Replaces the placeholder `BuildReportData()` with real data queries.

### D.1: Report Data Builder Interface

```csharp
public interface IReportDataBuilder
{
    string ReportType { get; }
    Task<ReportData> BuildAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? filters,
        CancellationToken ct);
}
```

**Registry pattern:** `ReportDataBuilderRegistry` holds `Dictionary<string, IReportDataBuilder>`. Injected into `ReportSchedulerService`. `BuildReportData()` delegates to registry:

```csharp
private async Task<ReportData> BuildReportData(ScheduledReport report, string primaryColor, CancellationToken ct)
{
    if (!_registry.TryGetBuilder(report.ReportType, out var builder))
        throw new PlatformException("UNKNOWN_REPORT_TYPE", $"No builder for '{report.ReportType}'");

    var to = _clock.UtcNow;
    var from = to.AddDays(-30); // Default; could parse from report.Filters
    var data = await builder.BuildAsync(report.TenantId, from, to, report.Filters, ct);
    data.PrimaryColor = primaryColor;
    return data;
}
```

**Files:**
- `src/Asterisk.Platform.Core/Reports/IReportDataBuilder.cs` — new
- `src/Asterisk.Platform.Api/Services/Reports/ReportDataBuilderRegistry.cs` — new
- `src/Asterisk.Platform.Api/Services/Reports/ReportSchedulerService.cs` — update BuildReportData

**Tests:** 2 tests — unknown type throws, delegates to correct builder

### D.2: Agent Performance Report Builder

**Type:** `"agent_performance"`

**Data source:** `IIntervalSnapshotStore` (from Pro.Analytics)

**Columns per agent:**
- Agent Name
- Calls Handled
- Avg Handle Time (seconds)
- Occupancy %
- Pause Time (minutes)
- Transfers
- RNA Count (Ring No Answer)

**Summary:** Totals and averages across all agents.

**Files:** `src/Asterisk.Platform.Api/Services/Reports/AgentPerformanceReportBuilder.cs`
**Tests:** 2 tests — generates rows per agent, empty data returns empty rows with zeroed summary

### D.3: Queue Analytics Report Builder

**Type:** `"queue_analytics"`

**Data source:** `IIntervalSnapshotStore`

**Columns per queue:**
- Queue Name
- Offered
- Answered
- Abandoned
- SLA %
- Avg Speed of Answer (seconds)
- Avg Handle Time (seconds)

**Summary:** Totals across all queues.

**Files:** `src/Asterisk.Platform.Api/Services/Reports/QueueAnalyticsReportBuilder.cs`
**Tests:** 2 tests — generates rows per queue, summary calculates weighted SLA

### D.4: Conversation Summary Report Builder

**Type:** `"conversation_summary"`

**Data source:** `IConversationStore` + `IMessageStore`

**Columns per channel:**
- Channel
- Total Conversations
- New Conversations
- Closed Conversations
- Avg Messages per Conversation
- Avg Resolution Time (hours)

**Summary:** Totals across all channels.

**Files:** `src/Asterisk.Platform.Api/Services/Reports/ConversationSummaryReportBuilder.cs`
**Tests:** 2 tests — generates rows per channel, handles no-data gracefully

### D.5: Report Type Validation

When creating a `ScheduledReport` via API (`POST /admin/reports`), validate that `ReportType` matches a registered builder. Return 400 with valid types if not found.

**Valid types:** `"agent_performance"`, `"queue_analytics"`, `"conversation_summary"`

**Files:** `src/Asterisk.Platform.Api/Endpoints/ScheduledReportEndpoints.cs` — add validation
**Tests:** 1 test — invalid type returns 400 with list of valid types

### D.6: Bot Analytics Aggregation

**Problem:** `BotAnalyticsCollector` emits events but nothing aggregates them. No `GET /analytics/bot` endpoint.

**Model:**

```csharp
public sealed class BotAnalyticsSummary
{
    public int TotalConversations { get; init; }
    public int HandedOff { get; init; }
    public int Resolved { get; init; }
    public int Failed { get; init; }
    public double HandoffRate { get; init; }        // HandedOff / Total
    public double ResolutionRate { get; init; }      // Resolved / Total
    public double AvgTurns { get; init; }
    public double FailureRate { get; init; }         // Failed / Total
}
```

**Store:**

```csharp
public interface IBotAnalyticsStore
{
    Task RecordEventAsync(TenantId tenantId, BotAnalyticsRecord record, CancellationToken ct);
    Task<BotAnalyticsSummary> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
```

**BotAnalyticsRecord:** `{ EventType, BotId, ConversationId, TurnCount, HandoffReason, Timestamp }`

**Wiring:** Subscribe to `BotAnalyticsCollector.Events` observable in a `BotAnalyticsPersistenceService` (BackgroundService) that calls `IBotAnalyticsStore.RecordEventAsync()`.

**Endpoint:** `GET /analytics/bot?from={date}&to={date}` (SupervisorPlus) returns `BotAnalyticsSummary`.

**Migration 014:** Table `bot_analytics`:
```sql
CREATE TABLE IF NOT EXISTS bot_analytics (
    id          BIGSERIAL PRIMARY KEY,
    tenant_id   TEXT NOT NULL,
    event_type  TEXT NOT NULL,
    bot_id      TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    turn_count  INTEGER NOT NULL DEFAULT 0,
    handoff_reason TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_bot_analytics_tenant_date
    ON bot_analytics (tenant_id, created_at);
```

**Files:**
- `src/Asterisk.Platform.Bot/BotAnalyticsRecord.cs` — new
- `src/Asterisk.Platform.Bot/BotAnalyticsSummary.cs` — new
- `src/Asterisk.Platform.Bot/IBotAnalyticsStore.cs` — new
- `src/Asterisk.Platform.Storage.InMemory/InMemoryBotAnalyticsStore.cs` — new
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresBotAnalyticsStore.cs` — new
- `src/Asterisk.Platform.Api/Services/BotAnalyticsPersistenceService.cs` — new
- `src/Asterisk.Platform.Api/Endpoints/AnalyticsEndpoints.cs` — add bot analytics endpoint

**Tests:** 4 tests — record event, summary calculates rates, empty period returns zeroes, tenant isolation

---

## Sub-project E: Production Hardening

Makes the platform deployable by Partners with confidence.

### E.1: Health Checks

Replace the empty `AddHealthChecks()` with real checks.

**PostgresHealthCheck:**
```csharp
public sealed class PostgresHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return HealthCheckResult.Healthy();
    }
}
```

**BackgroundServiceHealthCheck:** Each BackgroundService reports its last tick to `IServiceHeartbeat` singleton. Health check verifies all services ticked within 3x their expected interval.

```csharp
public interface IServiceHeartbeat
{
    void RecordTick(string serviceName, TimeSpan expectedInterval);
    bool IsHealthy(string serviceName);
    IReadOnlyDictionary<string, ServiceHeartbeatInfo> GetAll();
}
```

**AsteriskAmiHealthCheck:** Sends AMI `Action: Ping` if Asterisk is configured. Degraded (not unhealthy) if AMI unreachable — Platform can function without Asterisk for digital-only.

**Endpoint mapping:**
- `/health` — liveness probe (always 200 if process running)
- `/health/ready` — readiness probe (Postgres + background services + AMI)

**Registration:**
```csharp
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<BackgroundServiceHealthCheck>("services", tags: ["ready"])
    .AddCheck<AsteriskAmiHealthCheck>("asterisk", tags: ["ready"]);

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
```

Only register PostgresHealthCheck if Postgres connection string is configured.

**Files:**
- `src/Asterisk.Platform.Api/Health/PostgresHealthCheck.cs` — new
- `src/Asterisk.Platform.Api/Health/BackgroundServiceHealthCheck.cs` — new
- `src/Asterisk.Platform.Api/Health/AsteriskAmiHealthCheck.cs` — new
- `src/Asterisk.Platform.Api/Health/IServiceHeartbeat.cs` — new interface
- `src/Asterisk.Platform.Api/Health/ServiceHeartbeat.cs` — new implementation
- `src/Asterisk.Platform.Api/Program.cs` — update health check registration
- Background services (QueueDistributionWorker, ConversationTimeoutWorker, etc.) — add `RecordTick()` calls

**Tests:** 4 tests — postgres healthy, postgres unhealthy, service heartbeat timeout, liveness always 200

### E.2: Database Migration Runner

**Problem:** 13+ SQL migration files exist but must be applied manually via `psql`. No version tracking. Partners cannot deploy without SSH access to database.

**Implementation:** `DatabaseMigrationService : IHostedService`

```csharp
public sealed class DatabaseMigrationService : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Create tracking table
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _migrations (
                name TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """, ct);

        // Get applied migrations
        var applied = (await conn.QueryAsync<string>(
            "SELECT name FROM _migrations ORDER BY name", ct)).ToHashSet();

        // Get all migration files (embedded resources, sorted by name)
        var migrations = GetMigrationFiles().OrderBy(m => m.Name);

        foreach (var migration in migrations)
        {
            if (applied.Contains(migration.Name))
                continue;

            _logger.LogInformation("Applying migration {Name}", migration.Name);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(migration.Sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO _migrations (name) VALUES (@Name)",
                new { migration.Name }, tx);
            await tx.CommitAsync(ct);
        }
    }
}
```

**Migration files:** Embedded as assembly resources in `Asterisk.Platform.Storage.Postgres`. Read via `Assembly.GetManifestResourceStream()`.

**Startup order:** Runs BEFORE other hosted services (registered with `IHostedService` priority or explicit startup ordering).

**Failure behavior:** If any migration fails, the application does NOT start. Clear error message with migration name and SQL error.

**Only runs when Postgres is configured.** InMemory-only deployments skip entirely.

**Files:**
- `src/Asterisk.Platform.Storage.Postgres/DatabaseMigrationService.cs` — new
- `src/Asterisk.Platform.Storage.Postgres/Asterisk.Platform.Storage.Postgres.csproj` — embed SQL files as resources
- `src/Asterisk.Platform.Api/Program.cs` — register migration service (before other hosted services)

**Tests:** 3 tests — applies new migrations, skips applied, fails on bad SQL

### E.3: Config Validation at Startup

**Environment-aware validation:** Use `IHostEnvironment.IsProduction()` to enforce production-only requirements.

**Validations:**

| Config | Development | Production |
|--------|------------|------------|
| `Services:ServiceKey` | Default `"platform_internal_secret"` OK | **Required**, fail if missing |
| `Asterisk:Ami:Username/Password` | Default `"admin/admin"` OK | **Required** if `Asterisk:Ami:Hostname` set |
| `CORS_ORIGINS` | `"*"` OK | **Required**, fail if `"*"` or missing |
| `Jwt:KeyDirectory` | Auto-create | Must exist and be writable |

**Implementation:** Validation runs in `Program.cs` after configuration is built, before `builder.Build()`:

```csharp
if (app.Environment.IsProduction())
{
    var errors = new List<string>();
    if (serviceKey == "platform_internal_secret")
        errors.Add("Services:ServiceKey must be configured in production");
    if (corsOrigins.Contains("*"))
        errors.Add("CORS_ORIGINS must not be '*' in production");
    // ... etc
    if (errors.Count > 0)
        throw new InvalidOperationException(
            $"Configuration errors:\n{string.Join('\n', errors)}");
}
```

**Files:**
- `src/Asterisk.Platform.Api/Program.cs` — add validation block
- `src/Asterisk.Platform.Api/appsettings.json` — remove AMI default credentials
- `src/Asterisk.Platform.Api/appsettings.Development.json` — move defaults here

**Tests:** 3 tests — production fails without service key, development allows defaults, missing CORS in production fails

### E.4: Secrets Audit

**Changes:**

1. `appsettings.json`: Remove `"Username": "admin", "Password": "admin"` from Asterisk.Ami section
2. `appsettings.Development.json`: Add development defaults:
   ```json
   {
     "Asterisk": {
       "Ami": { "Username": "admin", "Password": "admin" }
     },
     "Services": { "ServiceKey": "platform_internal_secret" }
   }
   ```
3. `Program.cs`: Remove `?? "platform_internal_secret"` fallback for ServiceKey
4. Add `README` section documenting required environment variables for production

**Files:**
- `src/Asterisk.Platform.Api/appsettings.json` — remove secrets
- `src/Asterisk.Platform.Api/appsettings.Development.json` — add dev defaults
- `src/Asterisk.Platform.Api/Program.cs` — remove hardcoded fallbacks

**Tests:** No automated tests — verified by E.3 config validation tests

---

## Sub-project F: Twilio SMS + Cases API

Expands the market with the most-used SMS provider and unlocks the Cases feature.

### F.1: Twilio SMS Provider

**Implementation:** `TwilioSmsProvider` implements `ISmsProvider` using raw HTTP (no Twilio SDK — AOT-compatible).

```csharp
public sealed class TwilioSmsProvider : ISmsProvider
{
    private readonly HttpClient _client;
    private readonly TwilioOptions _options;

    public async Task<SmsSendResult> SendAsync(
        string from, string recipient, string body, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = recipient,
            ["From"] = from,
            ["Body"] = body,
        });

        var url = $"https://api.twilio.com/2010-04-01/Accounts/{_options.AccountSid}/Messages.json";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

        var response = await _client.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        // Parse MessageSid from response
        // Return SmsSendResult
    }
}
```

**TwilioOptions:**
```csharp
public sealed class TwilioOptions
{
    public required string AccountSid { get; set; }
    public required string AuthToken { get; set; }
}
```

**Files:**
- `src/Asterisk.Platform.Channels.Sms/Providers/TwilioSmsProvider.cs` — new
- `src/Asterisk.Platform.Channels.Sms/Providers/TwilioOptions.cs` — new
- `src/Asterisk.Platform.Channels.Sms/Providers/TwilioJsonContext.cs` — new (AOT)

**Tests:** 3 tests — send succeeds with mock HTTP, send fails maps error, get status parses response

### F.2: Twilio Webhook Inbound

**Problem:** `SmsWebhookHandler` currently only handles status updates (delivery receipts). It cannot process inbound SMS messages from Twilio.

**Fix:** Extend `SmsWebhookHandler.HandleAsync()` to detect and parse Twilio inbound format:

Twilio sends inbound messages as `application/x-www-form-urlencoded` POST with fields:
- `SmsSid`, `From`, `To`, `Body`
- `NumMedia`, `MediaUrl0..N`, `MediaContentType0..N`

**Detection logic:** If body contains `From` + `Body` fields and no `MessageStatus` field → inbound message.

**HMAC validation:** Twilio signs requests with `X-Twilio-Signature` header using HMAC-SHA1(AuthToken, URL + sorted params). Validate before processing.

**Media handling:** For each `MediaUrl{N}`, create `ImageBlock` or `FileBlock` based on `MediaContentType{N}`.

**Return:** `WebhookResult.NewMessage` with parsed `InboundMessage`.

**Files:**
- `src/Asterisk.Platform.Channels.Sms/SmsWebhookHandler.cs` — extend with inbound parsing
- `src/Asterisk.Platform.Channels.Sms/Providers/TwilioSignatureValidator.cs` — new

**Tests:** 4 tests — inbound text parsed, inbound with media creates blocks, HMAC validates, status update still works

### F.3: Conditional DI Registration

In `Program.cs`, register Twilio provider only when configured:

```csharp
var twilioSection = builder.Configuration.GetSection("Twilio");
if (!string.IsNullOrEmpty(twilioSection["AccountSid"]))
{
    builder.Services.Configure<TwilioOptions>(o =>
    {
        o.AccountSid = twilioSection["AccountSid"]!;
        o.AuthToken = twilioSection["AuthToken"]!;
    });
    builder.Services.AddHttpClient("twilio");
    builder.Services.AddSingleton<ISmsProvider, TwilioSmsProvider>();
}
```

If not configured, SMS remains without a provider (existing behavior — `SmsConnector.SendAsync` will fail gracefully when no `ISmsProvider` is registered).

**Files:** `src/Asterisk.Platform.Api/Program.cs`
**Tests:** 1 test — SMS works without Twilio configured (graceful failure)

### F.4: Cases API

**Problem:** `ICaseStore` and `Case` model exist with InMemory implementation, but zero API endpoints. The Cases feature is completely unusable.

**New file:** `CaseEndpoints.cs`

**Endpoints:**

```csharp
var group = app.MapGroup("/cases").RequireAuthorization("Authenticated");

group.MapGet("/", ListCases);          // filter by status, priority, agentId, contactId
group.MapGet("/{id}", GetCase);        // includes conversationIds list
group.MapPost("/", CreateCase);        // subject, priority, contactId, assignedAgentId?
group.MapPut("/{id}", UpdateCase);     // subject, status, priority, assignedAgentId
group.MapPost("/{id}/conversations/{conversationId}", LinkConversation);
```

**CreateCase flow:**
1. Validate contact exists via `IContactStore`
2. Generate `CaseNumber`: `"CASE-{timestamp-based-id}"` using `DateTimeOffset.UtcNow.Ticks % 100000000` formatted as 8-digit number (e.g., `"CASE-48291037"`). Unique enough for single-instance; Postgres UNIQUE constraint catches collisions
3. Create `Case` with status `Open`, save via `ICaseStore`
4. Return 201 Created

**LinkConversation flow:**
1. Validate conversation exists and belongs to tenant
2. Call `case.AddConversation(conversationId)`
3. Save case

**PostgresCaseStore:** `cases` table exists in migration 001. Verify schema matches `Case` model fields. If `case_number` column missing, add in migration 014.

**DTOs:**
- `CreateCaseRequest(string Subject, string Priority, string ContactId, string? AssignedAgentId)`
- `UpdateCaseRequest(string? Subject, string? Status, string? Priority, string? AssignedAgentId)`
- `CaseDto` — serializable response

**Files:**
- `src/Asterisk.Platform.Api/Endpoints/CaseEndpoints.cs` — new
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresCaseStore.cs` — new
- `src/Asterisk.Platform.Storage.Postgres/Migrations/014_CannedResponsesBotAnalytics.sql` — add cases columns if needed

**Tests:** 6 tests — create case, list with filters, get by id, update status, link conversation, tenant isolation

---

## Migration 014

Single migration file covering Sub-projects C, D, and F:

```sql
-- Canned responses (Sub-project C)
CREATE TABLE IF NOT EXISTS canned_responses (
    response_id TEXT NOT NULL,
    tenant_id   TEXT NOT NULL,
    shortcut    TEXT NOT NULL,
    title       TEXT NOT NULL,
    body        TEXT NOT NULL,
    category    TEXT,
    tags        TEXT,
    created_by  TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, response_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canned_responses_shortcut
    ON canned_responses (tenant_id, shortcut);

-- Bot analytics (Sub-project D)
CREATE TABLE IF NOT EXISTS bot_analytics (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    bot_id          TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    turn_count      INTEGER NOT NULL DEFAULT 0,
    handoff_reason  TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_bot_analytics_tenant_date
    ON bot_analytics (tenant_id, created_at);

-- Cases: verify existing schema, add case_number if missing
ALTER TABLE cases ADD COLUMN IF NOT EXISTS case_number TEXT;
```

---

## New Endpoint Summary

| Sub-project | Endpoint | Auth |
|-------------|----------|------|
| A | `POST /conversations/{id}/hold` | Authenticated |
| A | `POST /conversations/{id}/unhold` | Authenticated |
| A | `POST /conversations` | Authenticated |
| B | `POST /webchat/sessions` | AllowAnonymous |
| B | `GET /ws/webchat/{sessionId}` | AllowAnonymous (WebSocket) |
| B | `POST /webchat/sessions/{sessionId}/messages` | AllowAnonymous |
| C | `GET/POST/PUT/DELETE /admin/canned-responses` | AdminOnly |
| C | `GET /canned-responses` | Authenticated |
| C | `GET /supervisor/conversations` | SupervisorPlus |
| C | `GET /supervisor/conversations/{id}/messages` | SupervisorPlus |
| C | `POST /supervisor/conversations/{id}/takeover` | SupervisorPlus |
| C | `POST /supervisor/conversations/{id}/close` | SupervisorPlus |
| C | `POST /supervisor/conversations/{id}/note` | SupervisorPlus |
| D | `GET /analytics/bot` | SupervisorPlus |
| F | `GET/POST/PUT /cases` | Authenticated |
| F | `GET /cases/{id}` | Authenticated |
| F | `POST /cases/{id}/conversations/{conversationId}` | Authenticated |

**Total:** 20 new endpoints (57 → 77 endpoint groups estimated)

---

## Test Estimation

| Sub-project | New Tests |
|-------------|-----------|
| A: Critical Fixes | ~15 |
| B: WebChat E2E | ~12 |
| C: Agent Workspace | ~15 |
| D: Report Templates | ~13 |
| E: Production Hardening | ~10 |
| F: Twilio SMS + Cases | ~14 |
| **Total** | **~79** |

Current: 1,557 tests → Target: ~1,636 tests

---

## Files Changed Summary

**New files (~30):**
- 5 health check files (E.1)
- 3 WebChat transport files (B.1)
- 3 WebChat endpoint + widget files (B.2, B.3)
- 3 canned response files (C.1)
- 4 bot analytics files (D.6)
- 4 report builder files (D.1-D.4)
- 3 Twilio provider files (F.1)
- 2 case endpoint + postgres store files (F.4)
- 1 migration file
- 1 migration runner (E.2)
- 1 heartbeat interface + impl (E.1)

**Modified files (~15):**
- `WebhookEndpoints.cs` (A.1)
- `ConversationEndpoints.cs` (A.2, A.3)
- `ConversationSwitchboard.cs` + interface (A.2)
- `ErrorHandlingMiddleware.cs` (A.4)
- `SupervisorEndpoints.cs` (C.2)
- `SmsWebhookHandler.cs` (F.2)
- `ReportSchedulerService.cs` (D.1)
- `ScheduledReportEndpoints.cs` (D.5)
- `Program.cs` (all sub-projects)
- `appsettings.json` + `appsettings.Development.json` (E.4)
- `ApiJsonContext.cs` (new DTOs)
- Platform.Web: ~8 files (hooks, components)

---

## API Versioning

All new endpoints follow existing `/api/v1/` prefix. No version bump needed for additive endpoints.

## AOT Compatibility

All new code follows existing AOT patterns:
- `[JsonSerializable]` for new DTOs and WebSocket message types
- `[LoggerMessage]` for structured logging
- No reflection, no dynamic dispatch
- Class-based `{get; init;}` for Postgres row types (Npgsql 9 + Dapper)
