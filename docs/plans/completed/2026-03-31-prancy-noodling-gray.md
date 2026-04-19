# v1.2.0 "Monetization Ready" — Architecture & Decomposition

## Context

Asterisk.Platform tiene 27 paquetes, 1,068 tests, multi-tenancy 3 niveles, cluster management, y CDR/analytics ricos. Pero **no puede monetizarse como SaaS** porque carece de metering, billing, quota enforcement, y per-tenant observability. Los datos de consumo YA existen (CompletedSessionRow, Message, MediaFile, CallAttempt) — falta la capa de acumulación, rate cards, y UI.

**Market context:** CCaaS oculta 40-100% en costos. El diferenciador es billing transparente + self-hosted + data sovereignty.

---

## Data Sources That Already Exist (per-tenant)

| Source | Model | Fields útiles para billing | Location |
|--------|-------|---------------------------|----------|
| **Voice CDR** | `CompletedSessionRow` | TenantId, Direction, DurationMs, TalkTimeMs, QueueName, AgentId, RecordingName | Sdk.Pro.EventStore |
| **Messages** | `Message` | TenantId, Channel (WhatsApp/SMS/Email/etc), Direction, DeliveryStatus | Platform.Conversations |
| **Media/Recordings** | `MediaFile` | TenantId, SizeBytes, ContentType | Platform.Media |
| **Dialer Attempts** | `CallAttempt` | TenantId, CampaignId, DurationSeconds, Result | Sdk.Pro.Dialer |
| **Queue Intervals** | `IntervalSnapshot` | TenantId, CallsOffered/Answered/Abandoned, TotalTalkMs | Sdk.Pro.Analytics |
| **Agent Intervals** | `AgentSnapshot` | TenantId, AgentId, LoginDurationMs, TotalTalkMs | Sdk.Pro.Analytics |
| **AI Analysis** | `CallAnalysisResult` | TenantId, SessionId, ProcessingTime | Sdk.Pro.CallAnalytics |

## Enforcement Points That Already Exist (broken or missing)

| Gate | File | Status |
|------|------|--------|
| `OriginateGate.TryAcquire()` | Sdk.Pro.Dialer/Gate/OriginateGate.cs:55-66 | **Broken**: Activation strategies pass `0` for tenant limit |
| Campaign start | Platform.Api/Endpoints/CampaignEndpoints.cs:165 | **Missing**: No MaxActiveCampaigns check |
| Message send | Platform.Switchboard/DefaultConversationService.cs:74 | **Missing**: No per-message metering |
| Inbound pipeline | Platform.Channels.Core/Pipeline/InboundMessagePipeline.cs:41 | **Missing**: No quota step |
| Agent state | Platform.Api/Endpoints/AgentEndpoints.cs:44 | **Missing**: No active hours tracking |
| Tenant middleware | Platform.Api/Middleware/TenantResolutionMiddleware.cs:48 | **Enhancement**: Only stores TenantId, not Tenant+Options |

## Package Creation Pattern (established convention)

- Feature: `src/Asterisk.Platform.{Name}/` with `ServiceCollectionExtensions.cs` → `AddPlatform{Name}()`
- Store interfaces in feature package, implementations in Storage.InMemory + Storage.Postgres
- All stores accept `TenantId` first, `CancellationToken` last
- Tests: `tests/Asterisk.Platform.{Name}.Tests/` with xUnit + FluentAssertions + NSubstitute
- Registered in Program.cs after core, before storage

---

## Decomposition: 4 Sub-projects

### Sub-project A: Metering Engine + Quota Enforcement (Foundation)

**New package:** `Asterisk.Platform.Billing`

**Models:**
```
UsageRecord          — individual consumption event
├── RecordId         (EntityId)
├── TenantId         (TenantId)  
├── UsageType        (enum: VoiceInbound, VoiceOutbound, SmsInbound, SmsOutbound,
│                     WhatsAppInbound, WhatsAppOutbound, EmailInbound, EmailOutbound,
│                     WebChatSession, RecordingStorage, MediaStorage,
│                     DialerAttempt, DialerConnected, AgentLoginHour, AiAnalysis)
├── Quantity         (decimal — minutes, segments, bytes, count)
├── Unit             (enum: Minutes, Segments, Conversations, Bytes, Count, Hours)
├── Channel          (string? — channel identifier)
├── ReferenceId      (string? — sessionId, messageId, etc.)
├── RecordedAt       (DateTimeOffset)
└── Metadata         (Dictionary<string,string>?)

UsageSummary         — aggregated per tenant/period/type
├── TenantId
├── PeriodStart      (DateTimeOffset — month start)
├── PeriodEnd
├── UsageType
├── TotalQuantity    (decimal)
├── RecordCount      (int)
└── LastUpdatedAt

TenantQuota          — enforced limits (extends TenantOptions concept)
├── TenantId
├── MaxConcurrentChannels    (already exists in TenantOptions)
├── MaxActiveCampaigns       (already exists in TenantOptions)
├── MaxMonthlyVoiceMinutes   (NEW)
├── MaxMonthlyMessages       (NEW)
├── MaxStorageBytes          (NEW)
├── MaxActiveAgents          (NEW)
└── QuotaAction             (enum: Warn, SoftBlock, HardBlock)
```

**Store interfaces:**
```csharp
IUsageRecordStore
├── SaveAsync(UsageRecord, ct)
├── SaveBatchAsync(IReadOnlyList<UsageRecord>, ct)
├── GetSummaryAsync(TenantId, DateTimeOffset from, DateTimeOffset to, ct)
├── GetSummaryByTypeAsync(TenantId, UsageType, DateTimeOffset from, DateTimeOffset to, ct)

IUsageSummaryStore
├── UpsertAsync(UsageSummary, ct)
├── GetAsync(TenantId, DateTimeOffset periodStart, ct)
├── GetRangeAsync(TenantId, DateTimeOffset from, DateTimeOffset to, ct)
```

**Services:**
```csharp
IMeteringService
├── RecordUsageAsync(TenantId, UsageType, decimal quantity, string? referenceId, ct)
├── RecordBatchAsync(IReadOnlyList<UsageRecord>, ct)
├── GetCurrentPeriodSummaryAsync(TenantId, ct) → UsageSummary[]

IQuotaEnforcementService
├── CheckQuotaAsync(TenantId, UsageType, decimal additionalQuantity, ct) → QuotaCheckResult
├── GetQuotaStatusAsync(TenantId, ct) → TenantQuotaStatus
```

**Metering hooks (decorators/interceptors):**

1. **Voice metering** — Subscribe to `SessionCompletedEvent` from EventStore → `RecordUsageAsync(VoiceInbound/Outbound, durationMinutes)`
2. **Message metering** — Decorator on `IChannelConnector.SendAsync` → `RecordUsageAsync(SmsOutbound/WhatsAppOutbound/etc, 1)`
3. **Inbound message metering** — New pipeline step `MeteringStep` after dedup → `RecordUsageAsync(SmsInbound/WhatsAppInbound/etc, 1)`
4. **Storage metering** — Hook on `IMediaStore.SaveAsync` → `RecordUsageAsync(MediaStorage, sizeBytes)`
5. **Dialer metering** — Subscribe to `CallAttemptCompletedEvent` → `RecordUsageAsync(DialerAttempt/DialerConnected, 1)`
6. **Agent hours** — Hook on `AgentStateChangedEvent` → calculate delta, `RecordUsageAsync(AgentLoginHour, hours)`

**Quota enforcement fixes:**
1. Fix `OriginateGate` — pass `tenant.Options.MaxConcurrentChannels` instead of `0`
2. Add `MaxActiveCampaigns` check in `CampaignEndpoints.StartCampaign`
3. Enhance `TenantResolutionMiddleware` — load full Tenant into `HttpContext.Items["Tenant"]` (with cache)

**Estimated scope:** ~15 files new, ~8 files modified, ~40 tests

---

### Sub-project B: Rate Cards + Invoice Generation

**New models in Platform.Billing:**
```
RateCard             — pricing configuration per tenant
├── RateCardId       (EntityId)
├── TenantId         (TenantId — the tenant this rate card applies to)
├── Name             (string)
├── Currency         (string — "USD", "EUR")
├── EffectiveFrom    (DateTimeOffset)
├── EffectiveTo      (DateTimeOffset?)
├── Rates            (IReadOnlyList<RateEntry>)
└── IsDefault        (bool)

RateEntry            — per-usage-type pricing
├── UsageType        (UsageType enum)
├── UnitPrice        (decimal)
├── IncludedQuantity (decimal — free tier)
├── Tiers            (IReadOnlyList<RateTier>?)

RateTier             — volume-based pricing
├── FromQuantity     (decimal)
├── ToQuantity       (decimal?)
├── UnitPrice        (decimal)

Invoice              — generated billing document
├── InvoiceId        (EntityId)
├── TenantId
├── PeriodStart / PeriodEnd
├── Currency
├── LineItems        (IReadOnlyList<InvoiceLineItem>)
├── Subtotal / Tax / Total
├── Status           (Draft, Issued, Paid, Void)
├── GeneratedAt / IssuedAt / PaidAt

InvoiceLineItem
├── UsageType
├── Description
├── Quantity / UnitPrice / Amount
├── IncludedQuantity / OverageQuantity
```

**Invoice generation logic:** UsageSummary × RateCard → Invoice with line items, tier calculations, included quantities.

**Hierarchy rollup:** Customer invoice totals roll up to Partner summary, Partner to Platform.

**Estimated scope:** ~10 files new, ~3 modified, ~30 tests

---

### Sub-project C: Management API + Usage Dashboard

**New endpoints:**
```
GET    /api/management/tenants/{id}/usage          — Usage summary for period
GET    /api/management/tenants/{id}/usage/details   — Detailed usage records
GET    /api/management/tenants/{id}/quota            — Quota status + % consumed
GET    /api/management/rate-cards                    — List rate cards
POST   /api/management/rate-cards                    — Create rate card
PUT    /api/management/rate-cards/{id}               — Update rate card
DELETE /api/management/rate-cards/{id}               — Delete rate card
GET    /api/management/invoices                      — List invoices (filterable by tenant)
POST   /api/management/invoices/generate             — Generate invoice for period
GET    /api/management/invoices/{id}                 — Get invoice detail
POST   /api/management/invoices/{id}/issue           — Mark as issued
```

**Existing endpoint enhancement:**
- Add `?tenantId=` filter to `/api/analytics/cdr`, `/api/analytics/intervals`, `/api/analytics/dashboard`
- Platform admin can pass any tenantId to see per-tenant analytics

**Frontend (Platform.Web) new pages:**
```
/admin/tenants/{id}/usage       — Per-tenant usage dashboard (charts + table)
/admin/billing/rate-cards       — Rate card CRUD
/admin/billing/invoices         — Invoice list + detail viewer
/admin/tenants                  — Add usage column to tenant list table
```

**Estimated scope:** ~8 API files, ~6 frontend pages, ~20 tests + E2E

---

### Sub-project D: E2E Tests for Billing

Sprint 2 partial: billing-specific E2E tests covering:
- Usage dashboard display
- Rate card CRUD
- Invoice generation + viewing
- Quota warning display

**Estimated scope:** ~4 spec files, ~25 tests

---

## Implementation Sequence

```
Sub-project A ──→ Sub-project B ──→ Sub-project C ──→ Sub-project D
(Foundation)      (Billing logic)    (API + UI)        (E2E tests)
   ~2-3 days         ~2 days           ~2-3 days         ~1 day
```

A is required before B (B needs UsageSummary from A).
B is required before C (C needs invoices from B).
C is required before D (D tests C's UI).

**Total estimated:** ~8-10 days, 4 sub-projects, ~33 new files, ~115 tests

---

## Recommended First Step

**Sub-project A** is the foundation. It should be spec'd and planned as a standalone deliverable:

1. Create `Asterisk.Platform.Billing` package (models, interfaces, service)
2. InMemory + Postgres store implementations
3. Metering hooks (voice, messages, storage, dialer, agent hours)
4. Quota enforcement fixes (OriginateGate, campaign start, middleware)
5. Tests (40+)
6. Register in Program.cs

After A is complete, the platform can track and enforce consumption even without rate cards or invoices — which is already a massive operational improvement.

## Verification

After Sub-project A:
- `dotnet build` — 0 warnings
- `dotnet test` — all pass including ~40 new billing tests
- Demo: create calls → verify UsageRecords created
- Demo: exceed MaxConcurrentChannels → verify rejection
- Demo: exceed MaxActiveCampaigns → verify rejection
