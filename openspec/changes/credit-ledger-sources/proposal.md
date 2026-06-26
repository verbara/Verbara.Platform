---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Change **(c)** of the AI-credit-ledger program (ADR-0033). With the ledger as the live source of truth
(change b), this change adds the **prepaid revenue sources** — top-ups, promotional grants, partner
allocations — plus the public API surface and RBAC. It is purely **additive** (new grant sources + new
endpoints); no money-path rewrite.

> Full spec + tasks authored at the start of this change (after change b merges), re-grounded.

## What Changes

- **New grant sources** — `TopUp` (idempotent on `external_ref`, pre-authorised amount; payment rail
  deferred), `Promo` (with `expires_at`), `Partner` (allocation; partner draws feed the partner-revenue
  ledger, never the customer invoice).
- **Debit → source-lot allocation** — when ≥2 consumable sources coexist, each debit is allocated FIFO over
  open lots by `(billable_priority, expires_at, created_at)`; invoice customer-owed = debits drawn from the
  `PostPaid` lot; prepaid/promo/partner are never re-billed or cross-attributed.
- **Endpoints** — `POST /api/v1/.../credit-ledger/top-up` (operator/partner, `billing:credits:grant`),
  `GET …/balance` and `GET …/entries` (paginated `Core.PagedResult<T>`, `billing:credits:read`). DTOs in
  `ApiJsonContext`.
- **RBAC** — add `billing:credits:grant` (operator/partner-only this iteration) and `billing:credits:read`
  to `PermissionSeeder` + `RoleTemplateSeeder`; existing tenants via the `RbacReseed` CLI.

## Capabilities

### Modified Capabilities

- `ai-credit-ledger`: adds prepaid/promo/partner grant sources, FIFO lot allocation for multi-source
  invoicing, and the top-up/balance/entries API + RBAC. Delta authored at change start.

## Impact

- `Verbara.Platform.Billing` (lot allocation, `ICreditLedgerService`), `Verbara.Platform.Api` (endpoint
  group + DTOs + `ApiJsonContext`), `Storage.Postgres`/`Storage.InMemory` (allocation rows + queries),
  `PermissionSeeder`/`RoleTemplateSeeder` (+ `RbacReseed` for existing tenants). Web balance widget is a
  separate Platform.Web PR (deferred). Authoritative design: ADR-0033.
