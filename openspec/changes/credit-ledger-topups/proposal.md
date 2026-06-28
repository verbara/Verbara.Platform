---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Change **(c1)** of the AI-credit-ledger program (ADR-0033 + its 2026-06-27 (c) addendum). The substrate (a)
and the cutover (b) made the ledger the live source of truth for the monthly `Subscription` allowance. This
change adds the first **purchasable** grant source — **`TopUp`** — plus the public read API and RBAC, so the
business can sell prepaid credit immediately. It is deliberately the **low-risk, sellable half** of (c):
top-ups are **fungible** (a grant raises the balance, is consumed as a covered draw, lowers the `PostPaid`
tail), so the invoice math is already correct and **`PostMeteredDebitAsync` is untouched** — the (b)
characterization tests pass trivially. The lot-allocation machinery + `Promo`/`Partner` sources are the
separate, higher-risk **c2 `credit-ledger-lots`**.

## What Changes

- **`TopUp` grant source (fungible)** — an operator/partner mints a `TopUp` grant via the shipped
  `ICreditLedgerStore.PostGrantAsync` (idempotent on `external_ref`; positive `Amount`, `Source = TopUp`). No
  store debit/invoice change — a top-up just adds prepaid balance that the existing covered/PostPaid split
  spends correctly.
- **Operator top-up endpoint** — `POST /api/v1/management/credit-ledger/top-up` (gated `PlatformAdminOnly` +
  permission `billing:credits:grant`), body carries the target tenant, a positive credit amount, and a
  caller-supplied idempotency key (→ `external_ref`). Payment rail is deferred (the top-up mints credits).
- **Tenant read API** — `GET /api/v1/admin/credit-ledger/balance` (current O(1) balance) and
  `GET /api/v1/admin/credit-ledger/entries` (paginated `Core.PagedResult<CreditLedgerEntryDto>`), both
  `AdminOnly` + `RequireOperationalTenant` + permission `billing:credits:read`, tenant-scoped from
  `context.Items["TenantId"]`.
- **`GetEntriesCountAsync`** — new `ICreditLedgerStore` method (Postgres + InMemory) so the entries endpoint
  can populate `PagedResult<T>.TotalCount` (`GetEntriesAsync` returns no count today).
- **RBAC** — add `billing:credits:read` (to `PermissionSeeder` + `RoleTemplateSeeder.AllPermissions()` — tenant
  admins may read their own balance) and `billing:credits:grant` (to `PermissionSeeder` + **only**
  `platform_admin` and the hand-listed `partner_admin` array — **NOT** `AllPermissions()`, which would leak it
  to tenant `admin`/`system_admin`). Update the permission-count / role-template-count test assertions.
- **DTOs** — `TopUpRequest`, `CreditBalanceResponse`, `CreditLedgerEntryDto`, and
  `PagedResult<CreditLedgerEntryDto>` registered in `ApiJsonContext` (no anonymous objects; domain
  `CreditLedgerEntry` is never serialized).

## Capabilities

### Modified Capabilities

- `ai-credit-ledger`: adds the `TopUp` fungible grant source, the operator top-up + tenant balance/entries API,
  `GetEntriesCountAsync`, and the `billing:credits:*` RBAC. Delta in `specs/ai-credit-ledger/spec.md`.

## Impact

- `Verbara.Platform.Billing` — `ICreditLedgerStore.GetEntriesCountAsync`.
- `Storage.Postgres` / `Storage.InMemory` — `GetEntriesCountAsync` twins (deterministic `GetEntriesAsync`
  tiebreak so the twins agree at same-instant rows).
- `Verbara.Platform.Api` — new `CreditLedgerEndpoints` group + DTOs + `ApiJsonContext` + `Program.cs` mapping.
- `Storage.Postgres/Seeds` — `PermissionSeeder` + `RoleTemplateSeeder` (`billing:credits:grant`/`:read`); the
  `RbacReseed` CLI propagates `:read` to existing `platform_admin` only (documented).
- No migration (012 already reserves `source`/`external_ref`). Invoice + `PostMeteredDebitAsync` untouched.
  Authoritative design: ADR-0033 + the 2026-06-27 (c) addendum.
