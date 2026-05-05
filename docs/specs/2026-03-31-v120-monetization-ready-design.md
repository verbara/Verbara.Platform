# v1.2.0 "Monetization Ready" — Design Spec

> **Date:** 2026-03-31
> **Status:** APPROVED — Pending implementation
> **Prerequisite:** v1.1.0 complete (Plans 1-27 done)

## Problem Statement

Verbara.Platform has 27 packages, 1,068 tests, 3-tier multi-tenancy, cluster management, and rich CDR/analytics. But it **cannot be monetized as SaaS** because it lacks metering, billing, quota enforcement, and per-tenant observability. The consumption data already exists in CDR, messages, media, and dialer tables — what's missing is the accumulation layer, rate cards, quota enforcement, and management UI.

### Market Context

- CCaaS platforms hide 40-100% in costs beyond listed prices (telecom surcharges, AI overages, storage, compliance premiums)
- 51% of companies cite security/privacy as #1 concern with cloud CCaaS
- 32% of IT leaders face data sovereignty issues
- No Asterisk-based platform offers integrated billing + multi-tenant + cluster management
- **Differentiator:** Transparent billing + self-hosted + data sovereignty + AI pluggable

### Competitor Pricing Models

| Platform | Model | Range |
|----------|-------|-------|
| Amazon Connect | Per-second voice, per-msg | $0.018/min voice |
| Twilio Flex | Per-hour or per-user | $1/active user hour |
| Five9 | Per-seat tiered | $175-$325/agent/month |
| Genesys Cloud | Per-seat tiered | $75-$155/user/month |
| Zendesk | Outcome-based AI | Pay when AI resolves |

---

## Existing Data Sources (per-tenant, ready to meter)

| Source | Model | Billable Fields | Package |
|--------|-------|-----------------|---------|
| Voice CDR | `CompletedSessionRow` | Direction, DurationMs, TalkTimeMs, RecordingName | Sdk.Pro.EventStore |
| Messages | `Message` | Channel, Direction, DeliveryStatus | Platform.Conversations |
| Media | `MediaFile` | SizeBytes, ContentType | Platform.Media |
| Dialer | `CallAttempt` | CampaignId, DurationSeconds, Result | Sdk.Pro.Dialer |
| Queue Metrics | `IntervalSnapshot` | CallsOffered/Answered, TotalTalkMs | Sdk.Pro.Analytics |
| Agent Metrics | `AgentSnapshot` | LoginDurationMs, TotalTalkMs | Sdk.Pro.Analytics |
| AI Analysis | `CallAnalysisResult` | ProcessingTime | Sdk.Pro.CallAnalytics |

## Existing Enforcement Points (broken or missing)

| Gate | Location | Status |
|------|----------|--------|
| `OriginateGate.TryAcquire()` | Sdk.Pro.Dialer/Gate/OriginateGate.cs:55-66 | **Broken**: passes `0` for tenant limit |
| Campaign start | Platform.Api/Endpoints/CampaignEndpoints.cs:165 | **Missing**: no MaxActiveCampaigns check |
| Message send | Platform.Switchboard/DefaultConversationService.cs:74 | **Missing**: no metering |
| Inbound pipeline | Platform.Channels.Core/Pipeline/InboundMessagePipeline.cs:41 | **Missing**: no quota step |
| Agent state | Platform.Api/Endpoints/AgentEndpoints.cs:44 | **Missing**: no hours tracking |
| Tenant middleware | Platform.Api/Middleware/TenantResolutionMiddleware.cs:48 | **Enhancement needed**: only stores TenantId, not Tenant+Options |

---

## Architecture

### New Package: `Verbara.Platform.Billing`

Follows established convention: feature package with store interfaces, DI extension `AddPlatformBilling()`, InMemory + Postgres implementations in storage packages.

### Domain Models

#### UsageRecord — Individual consumption event

```csharp
public sealed class UsageRecord
{
    public required EntityId RecordId { get; init; }
    public required TenantId TenantId { get; init; }
    public required UsageType UsageType { get; init; }
    public required decimal Quantity { get; init; }
    public required UsageUnit Unit { get; init; }
    public string? Channel { get; init; }
    public string? ReferenceId { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
```

#### UsageType — What was consumed

```csharp
public enum UsageType
{
    VoiceInbound,
    VoiceOutbound,
    SmsInbound,
    SmsOutbound,
    WhatsAppInbound,
    WhatsAppOutbound,
    EmailInbound,
    EmailOutbound,
    WebChatSession,
    TelegramInbound,
    TelegramOutbound,
    RecordingStorage,
    MediaStorage,
    DialerAttempt,
    DialerConnected,
    AgentLoginHour,
    AiAnalysis,
}
```

#### UsageUnit — Measurement unit

```csharp
public enum UsageUnit
{
    Minutes,
    Segments,
    Conversations,
    Bytes,
    Count,
    Hours,
}
```

#### UsageSummary — Aggregated per tenant/period/type

```csharp
public sealed class UsageSummary
{
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required UsageType UsageType { get; init; }
    public required decimal TotalQuantity { get; set; }
    public required int RecordCount { get; set; }
    public required DateTimeOffset LastUpdatedAt { get; set; }
}
```

#### TenantQuota — Enforced limits

```csharp
public sealed class TenantQuota
{
    public required TenantId TenantId { get; init; }
    public int MaxConcurrentChannels { get; set; } = 100;
    public int MaxActiveCampaigns { get; set; } = 10;
    public long? MaxMonthlyVoiceMinutes { get; set; }
    public long? MaxMonthlyMessages { get; set; }
    public long? MaxStorageBytes { get; set; }
    public int? MaxActiveAgents { get; set; }
    public QuotaAction QuotaAction { get; set; } = QuotaAction.Warn;
}

public enum QuotaAction { Warn, SoftBlock, HardBlock }

public sealed record QuotaCheckResult(bool Allowed, string? Reason, double UsagePercent);
```

#### RateCard — Pricing configuration

```csharp
public sealed class RateCard
{
    public required EntityId RateCardId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public required IReadOnlyList<RateEntry> Rates { get; init; }
    public bool IsDefault { get; init; }
}

public sealed class RateEntry
{
    public required UsageType UsageType { get; init; }
    public required decimal UnitPrice { get; init; }
    public decimal IncludedQuantity { get; init; }
    public IReadOnlyList<RateTier>? Tiers { get; init; }
}

public sealed class RateTier
{
    public required decimal FromQuantity { get; init; }
    public decimal? ToQuantity { get; init; }
    public required decimal UnitPrice { get; init; }
}
```

#### Invoice — Generated billing document

```csharp
public sealed class Invoice
{
    public required EntityId InvoiceId { get; init; }
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<InvoiceLineItem> LineItems { get; init; }
    public required decimal Subtotal { get; init; }
    public decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public required DateTimeOffset GeneratedAt { get; init; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

public enum InvoiceStatus { Draft, Issued, Paid, Void }

public sealed class InvoiceLineItem
{
    public required UsageType UsageType { get; init; }
    public required string Description { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal Amount { get; init; }
    public decimal IncludedQuantity { get; init; }
    public decimal OverageQuantity { get; init; }
}
```

### Store Interfaces

```csharp
public interface IUsageRecordStore
{
    Task SaveAsync(UsageRecord record, CancellationToken ct);
    Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);
    Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public interface IRateCardStore
{
    Task SaveAsync(RateCard rateCard, CancellationToken ct);
    Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct);
    Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct);
    Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct);
}

public interface IInvoiceStore
{
    Task SaveAsync(Invoice invoice, CancellationToken ct);
    Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct);
    Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct);
    Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct);
}
```

### Services

```csharp
public interface IMeteringService
{
    Task RecordUsageAsync(TenantId tenantId, UsageType type, decimal quantity, string? referenceId, CancellationToken ct);
    Task RecordBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);
    Task<IReadOnlyList<UsageSummary>> GetCurrentPeriodSummaryAsync(TenantId tenantId, CancellationToken ct);
}

public interface IQuotaEnforcementService
{
    Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct);
    Task<TenantQuotaStatus> GetQuotaStatusAsync(TenantId tenantId, CancellationToken ct);
}

public interface IInvoiceGenerationService
{
    Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
}
```

### Metering Hooks

| Hook | Trigger | UsageType | Quantity |
|------|---------|-----------|----------|
| Voice completed | `SessionCompletedEvent` | VoiceInbound/Outbound | DurationMs / 60000 (minutes) |
| Message sent | `IChannelConnector.SendAsync` decorator | SmsOutbound/WhatsAppOutbound/etc | 1 per message |
| Message received | New `MeteringStep` in pipeline | SmsInbound/WhatsAppInbound/etc | 1 per message |
| Media uploaded | `IMediaStore.SaveAsync` hook | MediaStorage | SizeBytes |
| Dial attempt | `CallAttemptCompletedEvent` | DialerAttempt or DialerConnected | 1 per attempt |
| Agent hours | `AgentStateChangedEvent` | AgentLoginHour | delta hours |
| AI analysis | `CallAnalysisResult` saved | AiAnalysis | 1 per analysis |

### Quota Enforcement Fixes

1. **OriginateGate** — Pass `tenant.Options.MaxConcurrentChannels` instead of `0` in EventDrivenActivation, RateDrivenActivation, PreviewActivation
2. **Campaign start** — Check active campaign count against `MaxActiveCampaigns` before status transition
3. **Tenant middleware** — Cache-backed `ITenantStore` lookup, store `Tenant` + `TenantOptions` in `HttpContext.Items`

### Management API Endpoints

```
# Usage & Quotas
GET    /api/management/tenants/{id}/usage           — Usage summary for period
GET    /api/management/tenants/{id}/usage/details    — Detailed usage records
GET    /api/management/tenants/{id}/quota            — Quota status + % consumed

# Rate Cards
GET    /api/management/rate-cards                    — List rate cards
POST   /api/management/rate-cards                    — Create rate card
PUT    /api/management/rate-cards/{id}               — Update rate card
DELETE /api/management/rate-cards/{id}               — Delete rate card

# Invoices
GET    /api/management/invoices                      — List invoices
POST   /api/management/invoices/generate             — Generate invoice for period
GET    /api/management/invoices/{id}                 — Invoice detail
POST   /api/management/invoices/{id}/issue           — Mark as issued

# Existing endpoint enhancement
GET    /api/analytics/cdr?tenantId=                  — Filter CDR by tenant (platform admin only)
GET    /api/analytics/intervals?tenantId=            — Filter intervals by tenant
GET    /api/analytics/dashboard?tenantId=            — Filter dashboard by tenant
```

### Frontend Pages (Platform.Web)

```
/admin/tenants/{id}/usage       — Per-tenant usage dashboard (charts + breakdown table)
/admin/billing/rate-cards       — Rate card CRUD
/admin/billing/invoices         — Invoice list + detail viewer
/admin/tenants                  — Add usage/cost column to tenant list
```

---

## Decomposition: 4 Sub-projects

### Sub-project A: Metering Engine + Quota Enforcement (Foundation)

- New package `Verbara.Platform.Billing` (models, enums, interfaces, services)
- `IUsageRecordStore` — InMemory + Postgres implementations
- `IMeteringService` + `IQuotaEnforcementService` — default implementations
- Metering hooks (voice, messages, media, dialer, agent hours)
- Quota enforcement fixes (OriginateGate, campaign start, middleware)
- DI: `AddPlatformBilling()` + storage registration
- ~15 new files, ~8 modified, ~40 tests

### Sub-project B: Rate Cards + Invoice Generation

- `IRateCardStore` + `IInvoiceStore` — InMemory + Postgres
- `IInvoiceGenerationService` — calculates from UsageSummary × RateCard
- Tier calculations, included quantities, overage pricing
- Hierarchy rollup: Customer → Partner → Platform
- ~10 new files, ~3 modified, ~30 tests

### Sub-project C: Management API + Usage Dashboard

- 12 new management endpoints
- Tenant analytics filtering (enhance existing endpoints)
- 4 new frontend pages + tenant list enhancement
- ~8 API files, ~6 frontend pages, ~20 tests

### Sub-project D: E2E Tests for Billing

- Usage dashboard E2E
- Rate card CRUD E2E
- Invoice generation E2E
- Quota warning display E2E
- ~4 spec files, ~25 tests

## Implementation Sequence

```
A (Foundation)  →  B (Billing Logic)  →  C (API + UI)  →  D (E2E)
   ~2-3 days          ~2 days              ~2-3 days        ~1 day
```

**Total:** ~8-10 days, 4 sub-projects, ~33 new files, ~115 tests

## Database Schema (Postgres)

```sql
-- Sub-project A
CREATE TABLE usage_records (
    record_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    usage_type SMALLINT NOT NULL,
    quantity NUMERIC(18,6) NOT NULL,
    unit SMALLINT NOT NULL,
    channel TEXT,
    reference_id TEXT,
    recorded_at TIMESTAMPTZ NOT NULL,
    metadata JSONB
);
CREATE INDEX idx_usage_tenant_period ON usage_records (tenant_id, recorded_at DESC);
CREATE INDEX idx_usage_tenant_type ON usage_records (tenant_id, usage_type, recorded_at DESC);

-- Sub-project B
CREATE TABLE rate_cards (
    rate_card_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    currency TEXT NOT NULL DEFAULT 'USD',
    effective_from TIMESTAMPTZ NOT NULL,
    effective_to TIMESTAMPTZ,
    rates JSONB NOT NULL,
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_ratecard_tenant ON rate_cards (tenant_id, effective_from DESC);

CREATE TABLE invoices (
    invoice_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    period_start TIMESTAMPTZ NOT NULL,
    period_end TIMESTAMPTZ NOT NULL,
    currency TEXT NOT NULL,
    line_items JSONB NOT NULL,
    subtotal NUMERIC(18,2) NOT NULL,
    tax NUMERIC(18,2) NOT NULL DEFAULT 0,
    total NUMERIC(18,2) NOT NULL,
    status SMALLINT NOT NULL DEFAULT 0,
    generated_at TIMESTAMPTZ NOT NULL,
    issued_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ
);
CREATE INDEX idx_invoice_tenant ON invoices (tenant_id, period_start DESC);
```
