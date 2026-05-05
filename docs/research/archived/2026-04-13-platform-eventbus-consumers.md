# PlatformEventBus Consumers Audit (Sprint 1 Task A5)

**Date:** 2026-04-13
**Repo audited:** `Asterisk.Platform` (canonical bus lives there)
**Canonical file:** `src/Asterisk.Platform.Core/PlatformEventBus.cs` (168 lines)
**Purpose:** Inventory all publishers, subscribers, event types, DI wiring, and tests of `PlatformEventBus` so that Pillar 3 (tasks C1/C2) can convert it to a thin facade over `Sdk.Push.IPushEventBus` without breaking 35+ call sites.

---

## Publishers

All paths are relative to `/media/Data/Source/Verbara/Asterisk.Platform/`.

| File | Line | Event | Tenant source | User / target source |
|------|------|-------|---------------|----------------------|
| `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs` | 49 | `ConversationStateChangedEvent` (Queued enter) | `tenantId` param (`TenantId.Value`) | n/a |
| ` ` ↑ | 71 | `ConversationOfferedEvent` | `tenantId` param | `agentId` param |
| ` ` ↑ | 74 | `ConversationStateChangedEvent` (Queued→Offered) | `tenantId` param | n/a |
| ` ` ↑ | 103 | `ConversationAssignedEvent` (on Accept) | `tenantId` param | `agentId` param (queue/channel/contact passed empty) |
| ` ` ↑ | 105 | `ConversationStateChangedEvent` (Offered→Active) | `tenantId` param | n/a |
| ` ` ↑ | 127 | `ConversationStateChangedEvent` (Reject → Queued) | `tenantId` param | n/a |
| ` ` ↑ | 166 | `ConversationStateChangedEvent` (Transfer queue) | `tenantId` param | n/a |
| ` ` ↑ | 199 | `ConversationAssignedEvent` (Transfer to agent) | `tenantId` param | `agentId` param |
| ` ` ↑ | 230 | `ConversationStateChangedEvent` (Hold) | `tenantId` param | n/a |
| ` ` ↑ | 257 | `ConversationStateChangedEvent` (Resume) | `tenantId` param | n/a |
| ` ` ↑ | 284 | `ConversationStateChangedEvent` (Close) | `tenantId` param | n/a |
| `src/Asterisk.Platform.Switchboard/DefaultConversationService.cs` | 94 | `ConversationMessageEvent` | from conversation aggregate (`conv.TenantId.Value`) | sender = "agent"/"contact" string, no UserId |
| `src/Asterisk.Platform.Api/Services/AgentAssistBridge.cs` | 56 | `AgentAssistSuggestionEvent` | `session.CallSession?.TenantId?.ToString() ?? ""` | `session.CallSession?.AgentId?.ToString() ?? ""` |
| ` ` ↑ | 76 | `AgentAssistSentimentEvent` | same (closure capture) | same |
| ` ` ↑ | 95 | `AgentAssistComplianceAlertEvent` | same | same |
| ` ` ↑ | 113 | `AgentAssistTranscriptEvent` | same | same |
| `src/Asterisk.Platform.Api/Services/CampaignMetricsPoller.cs` | 58 | `CampaignMetricsUpdatedEvent` | from campaign row (`campaign.TenantId`) | n/a (tenant-broadcast) |
| ` ` ↑ | 71 | `CampaignMetricsUpdatedEvent` (zero-snapshot path) | same | n/a |
| `src/Asterisk.Platform.Api/Services/NotificationService.cs` | 135 | `NotificationEvent` | `tenantId` param (string) | `user.UserId.Value` (per-recipient loop) |
| `src/Asterisk.Platform.Api/Services/QueueDistributionWorker.cs` | 98 | `ConversationOfferedEvent` | `conv.TenantId.Value` | `agent.AgentId.Value` |
| `src/Asterisk.Platform.Api/Services/ConversationTimeoutWorker.cs` | 102 | `ConversationOfferExpiredEvent` | `conv.TenantId.Value` | `offer.AgentId.Value` |
| ` ` ↑ | 125 | `ConversationAbandonedEvent` | `conv.TenantId.Value` | n/a (queue-scoped) |
| ` ` ↑ | 148 | `ConversationStateChangedEvent` (auto-close idle) | `conv.TenantId.Value` | n/a |
| `src/Asterisk.Platform.Api/Endpoints/WebChatEndpoints.cs` | 146 | `ConversationMessageEvent` | from session/conv ctx | n/a (broadcast) |
| `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs` | 242 | `CampaignDispositionSubmittedEvent` | `tenantId.Value` (from `HttpContext.Items`) | agent JWT `sub` |
| `src/Asterisk.Platform.Api/Endpoints/SupervisorEndpoints.cs` | 156 | `ConversationAssignedEvent` (force-assign) | tenant from ctx | target agentId from body |
| ` ` ↑ | 208 | `ConversationMessageEvent` (whisper) | tenant from ctx | n/a |
| `src/Asterisk.Platform.Api/Endpoints/AgentEndpoints.cs` | 47 | `AgentStateChangedEvent` | `GetTenantId(context)` (`HttpContext.Items["TenantId"]`) | self (current agent from JWT `sub` → store lookup) |
| `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs` | 90 | `ConversationStateChangedEvent` (deliv. status) | from message ctx | n/a |
| ` ` ↑ | 94 | `ConversationMessageEvent` (inbound) | from message ctx | n/a |
| ` ` ↑ | 138 | `ConversationStateChangedEvent` (status update path) | from message ctx | n/a |
| `src/Asterisk.Platform.Api/Endpoints/CampaignEndpoints.cs` | 75, 212, 231, 250, 269 | `CampaignStatusChangedEvent` × 5 (start/pause/resume/stop/archive) | `tenantId` from ctx | n/a (tenant-broadcast) |

**Total publish call sites: 35.**

Tenant source patterns (4 distinct):
1. `TenantId` value-object param → `.Value` (Switchboard, workers).
2. Aggregate's own `TenantId` field (services that own state).
3. `HttpContext.Items["TenantId"]` cast (endpoints) — same shape as middleware sets.
4. String already (NotificationService param, AgentAssistBridge `.ToString()` from SDK CallSession).

User-targeted publishers: `NotificationEvent` only. All other "agent-scoped" events (offered, assigned, agent-state, agentassist.*) carry an `AgentId` field but SSE filter does **not** treat them as user-targeted — they are tenant-broadcast and filtered client-side (see Subscribers §).

---

## Subscribers

| File | Line | Subscription pattern | Filter logic |
|------|------|---------------------|--------------|
| `src/Asterisk.Platform.Api/Endpoints/SseEndpoints.cs` | 44–47 | `eventBus.Events.Where(...).Where(...).Subscribe(channel.Writer.TryWrite)` | (a) `e.TenantId == ctx.TenantId` if ctx tenant present, (b) `IsDeliverableToUser`: `NotificationEvent` requires `evt.UserId == userId` (JWT `sub`); all other event types pass through tenant-wide. Bounded channel (256, DropOldest) decouples Rx from response writer; per-event try/catch so one bad event does not kill the stream. |
| `src/Asterisk.Platform.Api/Services/RealtimeStateBridge.cs` | 42 | `_eventBus.Events.Subscribe(OnEvent)` (HostedService) | No Rx filter — `OnEvent` switches on event type to push state into Pro.Realtime stores. |
| `src/Asterisk.Platform.Api/Services/WebhookDispatcher.cs` | 39 | `eventBus.Events.Subscribe(OnEvent)` (HostedService) | No Rx filter — `OnEvent` matches event type → outbound webhook subscription lookup. |
| `src/Asterisk.Platform.Api/Services/AsteriskCapacitySyncService.cs` | 34–36 | `_eventBus.Events.OfType<AgentCapacityChangedEvent>().Subscribe(...)` (HostedService) | Single-type filter via `OfType<>`. |

**Total active subscribers: 4** (1 endpoint stream + 3 background services).

Note: `AgentAssistBridge` is a **publisher only** (subscribes to SDK Pro `AgentAssistSession.Suggestions/Sentiment/...` and republishes onto `PlatformEventBus`). It does not consume `PlatformEventBus`.

---

## Event Types (15, not 11 — CLAUDE.md was stale)

| Name | Fields (after `TenantId`/`Type`/`Timestamp` base) | Tenant-scoped | User-targeted |
|------|--------------------------------------------------|---------------|---------------|
| `ConversationAssignedEvent` | `ConversationId, AgentId, QueueName, Channel, ContactName` | ✓ | client-side (carries AgentId) |
| `ConversationMessageEvent` | `ConversationId, MessageId, Sender, Text` | ✓ | no |
| `ConversationStateChangedEvent` | `ConversationId, OldState, NewState` | ✓ | no |
| `AgentStateChangedEvent` | `AgentId, AgentName, OldState, NewState` | ✓ | client-side |
| `ConversationOfferedEvent` | `ConversationId, AgentId, QueueId` | ✓ | client-side |
| `ConversationOfferExpiredEvent` | `ConversationId, AgentId` | ✓ | client-side |
| `ConversationAbandonedEvent` | `ConversationId, QueueId` | ✓ | no |
| `AgentCapacityChangedEvent` | `AgentId, Channel, CurrentLoad, MaxLoad, CanAcceptVoice` | ✓ | client-side |
| `CampaignStatusChangedEvent` | `CampaignId, CampaignName, OldStatus, NewStatus` | ✓ | no |
| `CampaignMetricsUpdatedEvent` | `CampaignId, ContactsDialed, ContactsRemaining, ConnectRate, AbandonRate, ActiveCalls` | ✓ | no |
| `CampaignDispositionSubmittedEvent` | `CampaignId, DispositionCode, AgentId` | ✓ | client-side |
| `AgentAssistSuggestionEvent` | `SessionId, AgentId, SuggestionId, Text, Priority, Source, TriggerPhrase?` | ✓ | client-side |
| `AgentAssistSentimentEvent` | `SessionId, AgentId, Speaker, Score, Label, TriggerWords[]` | ✓ | client-side |
| `AgentAssistComplianceAlertEvent` | `SessionId, AgentId, RuleId, Phrase?, Severity` | ✓ | client-side |
| `AgentAssistTranscriptEvent` | `SessionId, AgentId, Speaker, Text, IsFinal` | ✓ | client-side |
| `NotificationEvent` | `NotificationId, UserId, Category, Severity, Title, Body, ActionUrl?` (overrides base `Timestamp`) | ✓ | **server-side** (only event the SSE endpoint server-side filters by `UserId`) |

**No event carries a `CorrelationId`.** None carries an explicit dispatch target other than `NotificationEvent.UserId`. AgentId on offer/assign/etc. is informational, not used by SseEndpoints to route.

Base record:
```csharp
public abstract record PlatformEvent(string TenantId, string Type, DateTimeOffset Timestamp);
```

---

## DI Registration

Single registration site (no per-package, no `TryAdd*` collisions):

```csharp
// src/Asterisk.Platform.Core/ServiceCollectionExtensions.cs:14-19
public static IServiceCollection AddPlatformCore(this IServiceCollection services)
{
    services.AddSingleton<IClock, SystemClock>();
    services.AddSingleton<PlatformEventBus>();
    services.TryAddSingleton<IFeatureRegistry, DefaultFeatureRegistry>();
    return services;
}
```

`Program.cs` does not register the bus directly — it wires via `builder.Services.AddPlatformCore();`. Bus is a concrete class (no interface), injected as `PlatformEventBus` everywhere.

---

## Tests Affected

No test uses NSubstitute on the bus — every test instantiates a **real** `PlatformEventBus` (`new PlatformEventBus()`) and asserts via `Events.Subscribe(...)`.

| Test file | Pattern |
|-----------|---------|
| `tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs:13, 492, 512, 540, 567` | Field `private readonly PlatformEventBus _eventBus = new();` + `_eventBus.Events.Subscribe(events.Add)` ×4 |
| `tests/Asterisk.Platform.Switchboard.Tests/DefaultConversationServiceTests.cs:20` | Same field pattern, asserts on emitted events |
| `tests/Asterisk.Platform.Api.Tests/AsteriskCapacitySyncServiceTests.cs:17` | Real bus + drives `AgentCapacityChangedEvent` through `Publish` |
| `tests/Asterisk.Platform.Api.Tests/ConversationTimeoutWorkerTests.cs:19, 201` | Real bus + `Subscribe(events.Add)` |
| `tests/Asterisk.Platform.Api.Tests/QueueDistributionWorkerTests.cs:21` | Real bus |
| `tests/Asterisk.Platform.Api.Tests/Services/WebhookDispatcherTests.cs:12` | Real bus drives `OnEvent` |
| `tests/Asterisk.Platform.Api.Tests/RealtimeStateBridgeTests.cs:24, 38–39` | Comment: "Wire the real PlatformEventBus" — direct construction |
| `tests/Asterisk.Platform.Api.Tests/NotificationServiceTests.cs:24, 136` | Real bus + assertion on `NotificationEvent` per-user emission |

Other `.Events.Subscribe(...)` hits in `tests/Asterisk.Platform.Bot.Tests/BotAnalyticsCollectorTests.cs` and `BotOrchestratorTests.cs` are on `BotAnalyticsCollector.Events` — **a different observable, not `PlatformEventBus`** (false positive in grep, ignored).

---

## Migration Impact Summary (C1/C2 facade refactor)

When `PlatformEvent` becomes `: Sdk.Push.PushEvent` and `PlatformEventBus` delegates to `IPushEventBus`:

1. **Publishers (35 sites): zero changes.** They all call `_eventBus.Publish(new XxxEvent(...))`. Facade keeps the `Publish(PlatformEvent)` method — internal forwarding to `IPushEventBus.Publish` is invisible to callers.
2. **Subscribers (4 sites): zero changes** **iff** `Events` property and event-record types stay the same shape. SseEndpoints' `IsDeliverableToUser` switches on `evt is NotificationEvent` — must keep that subtype intact, not collapse into a generic `PushEvent` envelope. Same constraint on `AsteriskCapacitySyncService` (`OfType<AgentCapacityChangedEvent>`) and `RealtimeStateBridge` / `WebhookDispatcher` (type-switch on concrete records).
3. **Tests (8 files): zero changes** if `new PlatformEventBus()` parameterless constructor remains. If facade requires injecting `IPushEventBus`, every test needs a constructor update — recommend keeping a parameterless overload that internally uses an in-process default (Subject-backed).
4. **DI (1 site): one-line change.** `AddPlatformCore` may need to also register `IPushEventBus` (default in-memory) before `PlatformEventBus` so resolution works. Singleton scope must be preserved (subscribers grab the instance once in HostedService startup).
5. **Risk of breaking change:** LOW for publishers/subscribers (closed set, clearly contained). MEDIUM for serialization — SSE writes `JsonSerializer.Serialize(evt, evt.GetType(), ApiJsonContext)`; if `PushEvent` adds a base property (e.g. `EventId`), ApiJsonContext source-gen needs regen and the wire format gains a field (frontend should ignore unknown fields). HIGH-attention items: (a) `NotificationEvent` overrides the base `Timestamp` parameter — verify this still works when `PushEvent` defines its own Timestamp; (b) the four `AgentAssist*` events use empty-string fallbacks for tenant — facade should not assume non-empty TenantId for routing.
6. **Recommended pre-change shim:** keep `PlatformEvent` as the *public* base (alias/inherit from `PushEvent`), keep all 16 records as-is, keep `PlatformEventBus.Events` returning `IObservable<PlatformEvent>`. The `IPushEventBus` becomes an *implementation detail* exposed via a second property (e.g. `bus.Push`) for new consumers — preventing the 35-site publisher rewrite.

**No publisher uses `OnNext` directly** — all go through `Publish`. Single chokepoint makes the facade safe.
