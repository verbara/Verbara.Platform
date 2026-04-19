# Sprint 3: Partner Portal + Partner Billing — Design Spec

**Date:** 2026-04-07
**Sprint:** v1.4.0 Sprint 3
**Decisions:** #3 (Partner Model) + #11 (Partner Billing)
**Depends on:** Sprint 0 (security fixes), Sprint 1 (suspension + settings facade), Sprint 2 (feature flags + dunning)

---

## Goal

Enable Partner tenants to operate as autonomous resellers: manage their own Customer tenants, define markup pricing via RateCards, generate invoices, and track revenue — all through a dedicated `/partner/*` API surface with its own auth handler, permissions, and role templates.

## Architecture

Partners are tenants with `TenantType.Partner`, direct children of the Platform tenant. They create and manage Customer tenants under themselves (max hierarchy depth 3: Platform → Partner → Customer). The Partner Portal is a dedicated set of 19 endpoints under `/partner/*` with a `PartnerAdminOnly` authorization policy. Partner billing uses the existing RateCard model — Partners define their own RateCards with markup prices. Revenue is tracked via `PartnerRevenueRecord` snapshots created at invoice generation time.

## Non-Goals (Deferred)

- **Payment gateway integration** (Stripe) — v2.0
- **Consolidated Platform→Partner invoice** (aggregate cost bill) — v2.0
- **Automated invoice generation** (scheduled) — v2.0
- **Self-service plan upgrade by Partners** — v2.0
- **Partner DELETE customer endpoint** (suspend-only for now; Platform does soft-delete) — v2.0
- **Partner plan catalog** (Partners define which plans they offer) — v1.5.0
- **Add-ons with quotas per add-on** — v1.5.0
- **Auto-cascade parent plan downgrade to children** — v1.5.0

---

## Section 1: Data Model

### New Models

#### PartnerRevenueRecord (Platform.Billing)

```csharp
public sealed class PartnerRevenueRecord
{
    public required EntityId RevenueId { get; init; }
    public required TenantId PartnerTenantId { get; init; }
    public required TenantId CustomerTenantId { get; init; }
    public required EntityId InvoiceId { get; init; }
    public required decimal GrossAmount { get; init; }     // Total invoiced to Customer (Partner RateCard)
    public required decimal PlatformCost { get; init; }    // Cost at Platform base rates
    public required decimal PartnerMargin { get; init; }   // Gross - Platform = Partner earnings
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

#### IPartnerRevenueStore (Platform.Billing)

```csharp
public interface IPartnerRevenueStore
{
    ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(TenantId partnerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);
    ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct = default);
}
```

### Existing Models Reused (No Changes)

- **RateCard** — Partner creates its own RateCard (`TenantId = partnerId`). Customers use Partner's RateCard for invoicing. Platform has a base RateCard (`IsDefault = true` on host tenant).
- **Invoice** — No changes. Customer invoice generated with Partner's RateCard prices.
- **Tenant** — No changes. `ParentTenantId` + `TenantType` model the hierarchy.
- **InvoiceLineItem** — No changes. Line items calculated from Partner's RateEntry prices.

---

## Section 2: Permissions & Role Templates

### New Permissions (8)

Category: `partner`

| Permission ID | Description |
|--------------|-------------|
| `partner:customer:view` | View partner's child customer tenants |
| `partner:customer:create` | Create customer tenants under this partner |
| `partner:customer:manage` | Edit settings, suspend, and activate child customers |
| `partner:customer:delete` | Delete child customer tenants (soft delete) — permission seeded now, endpoint deferred to v2.0 |
| `partner:billing:view` | View invoices, usage, and revenue for child customers |
| `partner:billing:manage` | Create rate cards, generate invoices, manage quotas |
| `partner:settings:view` | View partner's own tenant settings |
| `partner:settings:manage` | Edit partner's own operational and auth settings |

Total permissions: 68 (60 existing + 8 new).

### New Role Templates (3)

| Template ID | Name | Permissions |
|-------------|------|-------------|
| `partner_admin` | Partner Admin | All 8 `partner:*` permissions |
| `partner_billing` | Partner Billing | `partner:customer:view`, `partner:billing:view`, `partner:billing:manage` |
| `partner_viewer` | Partner Viewer | `partner:customer:view`, `partner:billing:view`, `partner:settings:view` |

Total role templates: 11 (8 existing + 3 new).

### Seeding

- `PermissionSeeder` — add 8 `partner:*` permission definitions
- `RoleTemplateSeeder` — add 3 partner role templates

---

## Section 3: Auth & Authorization

### PartnerAdminRequirement + Handler

```csharp
public sealed class PartnerAdminRequirement : IAuthorizationRequirement { }

public sealed class PartnerAdminAuthorizationHandler
    : AuthorizationHandler<PartnerAdminRequirement>
{
    // Injected: ITenantStore
    // Logic:
    // 1. Extract "tid" claim from JWT
    // 2. Load tenant from ITenantStore
    // 3. Validate tenant.Type == TenantType.Partner
    // 4. Validate tenant.Status is Active, Warning, or Degraded (not Suspended/Deleted/PendingDeletion)
    // 5. If valid → context.Succeed(requirement)
    // 6. If invalid → silent fail (ASP.NET returns 403)
}
```

### Authorization Policy

```csharp
options.AddPolicy("PartnerAdminOnly", p =>
    p.AddRequirements(new PartnerAdminRequirement()));
```

### Dual Gate on Endpoints

Each `/partner/*` endpoint uses two authorization layers:

1. **`PartnerAdminOnly` policy** — ensures caller is from a Partner tenant
2. **Permission check** — verifies specific `partner:*` permission via existing `PermissionAuthorizationHandler`

```csharp
group.MapGet("/partner/customers", handler)
    .RequireAuthorization("PartnerAdminOnly")
    .RequireAuthorization("partner:customer:view");
```

### Scope Enforcement

All `/partner/*` endpoints that operate on a Customer validate ownership:

```csharp
var customer = await tenantStore.GetAsync(customerId, ct);
if (customer is null || customer.ParentTenantId != callerTenantId)
    return Results.NotFound(); // Does not reveal existence
```

Same pattern as `ManagementTenantEndpoints` ownership validation but scoped to Partner's sub-tree.

### Platform Admin Interaction

Platform Admin continues using `/management/*` to manage Partners. `/partner/*` endpoints are exclusive to Partner tenants — Platform Admin does not use them.

### Feature Gates

`/partner/*` endpoints do **NOT** use `RequirePlanFeature`. The Partner Portal is a tenant-type capability, not a plan-gated feature. If the Partner is on Starter plan, their features are limited by the plan — but the portal itself is always accessible.

---

## Section 4: Endpoints (19 total)

### Partner Customer Management (6 endpoints)

File: `PartnerCustomerEndpoints.cs`

| Method | Route | Permission | Description |
|--------|-------|------------|-------------|
| GET | `/partner/customers` | `partner:customer:view` | List partner's child customers (filters: status, plan) |
| POST | `/partner/customers` | `partner:customer:create` | Create customer (ParentTenantId = caller, Type = Customer) |
| GET | `/partner/customers/{id}` | `partner:customer:view` | Get customer detail |
| PUT | `/partner/customers/{id}` | `partner:customer:manage` | Update name, metadata, options |
| POST | `/partner/customers/{id}/suspend` | `partner:customer:manage` | Suspend customer |
| POST | `/partner/customers/{id}/activate` | `partner:customer:manage` | Reactivate customer |

**CreateCustomer validation:**
- `Type` forced to `Customer` (cannot create Partner under Partner)
- `ParentTenantId` forced to caller's tenantId
- Plan cannot exceed Partner's own plan (hierarchy ceiling)
- Partner must be Active to create children

**No DELETE endpoint** — Partners suspend; Platform Admin does soft-delete via `/management/*`. Deferred to v2.0.

### Partner Customer Settings (2 endpoints)

File: `PartnerCustomerEndpoints.cs` (same file)

| Method | Route | Permission | Description |
|--------|-------|------------|-------------|
| GET | `/partner/customers/{id}/settings` | `partner:customer:manage` | Customer settings (reuses `BuildSettingsDto`) |
| PUT | `/partner/customers/{id}/settings` | `partner:customer:manage` | Update customer settings |

**PUT restrictions:**
- Partner can write: Operational, Auth, Plan, AddOns
- Plan must satisfy hierarchy ceiling (plan ≤ partner's plan)
- Partner cannot write: Quotas, RateLimitTier (derived from plan or controlled by Platform)
- Reuses `TenantSettingsEndpoints.BuildSettingsDto()` and `ApplyUpdates()` with Partner-level restrictions

### Partner Billing (7 endpoints)

File: `PartnerBillingEndpoints.cs`

| Method | Route | Permission | Description |
|--------|-------|------------|-------------|
| GET | `/partner/rate-cards` | `partner:billing:manage` | List partner's rate cards |
| POST | `/partner/rate-cards` | `partner:billing:manage` | Create rate card (markup pricing) |
| PUT | `/partner/rate-cards/{id}` | `partner:billing:manage` | Update rate card |
| DELETE | `/partner/rate-cards/{id}` | `partner:billing:manage` | Delete rate card |
| GET | `/partner/customers/{id}/invoices` | `partner:billing:view` | List invoices for a customer |
| POST | `/partner/customers/{id}/invoices/generate?from={iso}&to={iso}` | `partner:billing:manage` | Generate invoice for period (Partner RateCard + PartnerRevenueRecord) |
| GET | `/partner/customers/{id}/usage?from={iso}&to={iso}` | `partner:billing:view` | Usage summary for a customer (period filter) |

### Partner Revenue Dashboard (2 endpoints)

File: `PartnerRevenueEndpoints.cs`

| Method | Route | Permission | Description |
|--------|-------|------------|-------------|
| GET | `/partner/revenue` | `partner:billing:view` | Aggregated revenue summary (total gross, cost, margin by period) |
| GET | `/partner/revenue/details` | `partner:billing:view` | Revenue detail per customer per period |

### Partner Settings (2 endpoints)

File: `PartnerSettingsEndpoints.cs`

| Method | Route | Permission | Description |
|--------|-------|------------|-------------|
| GET | `/partner/settings` | `partner:settings:view` | Partner's own settings (plan/tier are read-only) |
| PUT | `/partner/settings` | `partner:settings:manage` | Update own settings (Operational + Auth only) |

---

## Section 5: Invoice Generation with Markup

### Flow

```
POST /partner/customers/{id}/invoices/generate
  │
  ├── 1. Validate ownership (customer.ParentTenantId == caller)
  ├── 2. Load customer usage for period (IMeteringService)
  ├── 3. Load Partner's RateCard (IRateCardStore with partnerId)
  │      └── If none → 400 "No rate card configured"
  ├── 4. Generate Invoice to Customer using Partner's RateCard
  │      └── Reuses IInvoiceGenerationService.GenerateAsync()
  ├── 5. Load Platform base RateCard (IsDefault=true on host tenant)
  │      └── If none → 400 "No platform base rate card configured"
  ├── 6. Calculate platform cost: usage × base rates
  ├── 7. Create PartnerRevenueRecord:
  │      ├── GrossAmount = invoice.Total
  │      ├── PlatformCost = step 6 result
  │      └── PartnerMargin = GrossAmount - PlatformCost
  └── 8. Return invoice + revenue snapshot
```

### Example

```
Customer "Acme" used in March:
  - 500 voice minutes
  - 1,000 SMS

Platform base RateCard:
  - Voice: $0.02/min  → 500 × $0.02 = $10.00
  - SMS:   $0.01/msg  → 1000 × $0.01 = $10.00
  Platform cost = $20.00

Partner "TelcoX" RateCard:
  - Voice: $0.035/min → 500 × $0.035 = $17.50
  - SMS:   $0.02/msg  → 1000 × $0.02  = $20.00
  Customer invoice = $37.50

PartnerRevenueRecord:
  GrossAmount   = $37.50
  PlatformCost  = $20.00
  PartnerMargin = $17.50
```

### Platform Base RateCard

The host tenant's RateCard with `IsDefault = true` serves as the base rate for margin calculation. No new model needed — existing RateCard infrastructure is reused. If the host tenant has no default RateCard, invoice generation with revenue tracking returns an error.

---

## Section 6: Storage

### InMemoryPartnerRevenueStore

- `ConcurrentDictionary<string, PartnerRevenueRecord>` keyed by RevenueId
- ListAsync filters by PartnerTenantId + date range via LINQ
- Registered as singleton in `AddInMemoryStorage()`

### PostgresPartnerRevenueStore

- Migration 009: `partner_revenue` table
- Columns: `revenue_id PK`, `partner_tenant_id`, `customer_tenant_id`, `invoice_id`, `gross_amount NUMERIC(18,4)`, `platform_cost NUMERIC(18,4)`, `partner_margin NUMERIC(18,4)`, `period_start TIMESTAMPTZ`, `period_end TIMESTAMPTZ`, `created_at TIMESTAMPTZ`
- Index: `(partner_tenant_id, period_start)`
- Registered as singleton in `AddPostgresStorage()`
- Class-based row type with `{get; init;}` (Dapper + Npgsql 9 requirement)

---

## Section 7: DI & Wiring

### Program.cs additions

```csharp
// Auth — new policy + handler
options.AddPolicy("PartnerAdminOnly", p =>
    p.AddRequirements(new PartnerAdminRequirement()));
services.AddSingleton<IAuthorizationHandler, PartnerAdminAuthorizationHandler>();

// Endpoint mapping
v1.MapPartnerCustomerEndpoints();
v1.MapPartnerBillingEndpoints();
v1.MapPartnerRevenueEndpoints();
v1.MapPartnerSettingsEndpoints();
```

### ApiJsonContext additions

New DTOs to register:
- `PartnerCustomerDto`, `PartnerCustomerListDto`
- `CreatePartnerCustomerRequest`, `UpdatePartnerCustomerRequest`
- `PartnerRevenueDto`, `PartnerRevenueSummaryDto`
- `PartnerSettingsDto`, `UpdatePartnerSettingsRequest`

Reused (already registered): `TenantSettingsDto`, `RateCardDto`, `InvoiceDto`, `UsageSummaryDto`, `InvoiceLineItemDto`

### Seed updates

- `PermissionSeeder` — 8 new `partner:*` permissions
- `RoleTemplateSeeder` — 3 new partner role templates

---

## Section 8: Testing (~30 new tests)

| Test File | Count | Coverage |
|-----------|-------|----------|
| `PartnerAdminAuthorizationHandlerTests` | 4 | Partner OK, Customer deny, Platform deny, Suspended deny |
| `PartnerRevenueCalculationTests` | 4 | Basic margin, zero usage, tiered pricing, no base rate card error |
| `PartnerCustomerEndpointsTests` | 8 | CRUD + ownership validation + hierarchy ceiling + status filter |
| `PartnerBillingEndpointsTests` | 6 | Rate cards CRUD + invoice generate + revenue record creation |
| `PartnerRevenueEndpointsTests` | 4 | Summary, details, date range filter, empty result |
| `PartnerSettingsEndpointsTests` | 4 | Get, update, restricted fields rejected, ownership validation |

Test factories: `SeedPartnerTenant` helper creates Partner tenant + PartnerAdmin role + FeatureGateCache entry.

Expected test count: 1446 → ~1476.

---

## File Inventory

| Category | New Files | Modified Files |
|----------|-----------|----------------|
| Models/Interfaces | 2 (PartnerRevenueRecord, IPartnerRevenueStore) | 0 |
| Auth | 2 (PartnerAdminRequirement, PartnerAdminAuthorizationHandler) | 0 |
| Endpoints | 4 (Customer, Billing, Revenue, Settings) | 0 |
| Storage InMemory | 1 (InMemoryPartnerRevenueStore) | 1 (ServiceCollectionExtensions) |
| Storage Postgres | 1 (PostgresPartnerRevenueStore) | 1 (ServiceCollectionExtensions) + migration 009 |
| Seeds | 0 | 2 (PermissionSeeder, RoleTemplateSeeder) |
| Serialization | 0 | 1 (ApiJsonContext) |
| Program.cs | 0 | 1 |
| Tests | 6 new test files | 2 (test factories) |
| **Total** | **16 new** | **8 modified** |
