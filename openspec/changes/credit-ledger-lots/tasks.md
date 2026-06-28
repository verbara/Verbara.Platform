# Tasks — credit-ledger-lots (c2)

> Authoritative design: ADR-0033 + the **2026-06-28 (c2) resolution addendum**. Money-path rewrite — implement as
> **sequential always-green FCM groups** (build + test + commit between each), the cadence proven on (a)/(b)/c1.
> CI does NOT run the Postgres suite → the **InMemory twin carries every behavioral test**.

## Group 1 — Lot substrate (migration + domain types), inert

- [x] 1.1 Migration `013_credit_lots.sql` (additive, `IF NOT EXISTS`): `credit_lot` (`lot_id` TEXT PK = grant
  `entry_id`, `tenant_id` TEXT, `source` SMALLINT, `original` NUMERIC(18,6), `remaining` NUMERIC(18,6)
  `CHECK (remaining >= 0)`, `expires_at` TIMESTAMPTZ NULL, `granted_at` TIMESTAMPTZ, `lot_seq` BIGINT) +
  `credit_allocation` (`allocation_id` TEXT PK, `debit_entry_id` TEXT, `lot_id` TEXT, `source` SMALLINT, `amount`
  NUMERIC(18,6), `created_at` TIMESTAMPTZ). Indexes: FIFO/lock index on `credit_lot (tenant_id, source, expires_at,
  granted_at, lot_seq)`; `idx_credit_allocation_debit (debit_entry_id)`; `idx_credit_allocation_lot (lot_id)`. A
  per-tenant `lot_seq` source (a `credit_lot_seq` table `(tenant_id PK, next_seq BIGINT)` bumped under the same tx, or
  `MAX(lot_seq)+1` under the projection lock — pick the lock-consistent one). **Back-fill block**: `INSERT INTO
  credit_lot SELECT … remaining = balance, source = 0 (Subscription), lot_seq = 0, expires_at = NULL FROM
  tenant_credit_balance WHERE balance > 0` so `Σ remaining == balance` from day one.
- [x] 1.2 Domain types (Billing): `CreditLot` (class, `{ get; init; }`, no Npgsql), `CreditAllocation`, the static
  `BillablePriority` map (`Promo=0, Partner=1, Subscription=2, TopUp=2`), and a `SourceRemaining` readonly record for
  reporting. Add `enum`/const as needed. NO behavior wired yet → build stays green, ledger still inert.

## Group 2 — Store contract + grant-time lot mint (both twins)

- [x] 2.1 `ICreditLedgerStore`: add `GetRemainingBySourceAsync(TenantId, DateTimeOffset now, ct)` →
  `IReadOnlyList<SourceRemaining>` (open, non-expired remaining per source; excludes expired Promo + zero lots +
  PostPaid; `Σ == GetBalanceAsync` documented as the invariant) and `GetLotsAsync(TenantId, ct)` (test/diagnostic
  raw-lot read). The expiry-sweep contract (`GetExpiredLotsAsync` / `ReclaimExpiredLotAsync`) is **Group 4** (the
  reclaim sweeper), not Group 2 — moved there.
- [x] 2.2 `PostGrantAsync` (both stores): in the `if (inserted == 1)` block, also insert the `credit_lot` row
  (`remaining = original = grant.Amount`, `source = grant.Source`, `expires_at = grant.ExpiresAt`, `granted_at =
  grant.CreatedAt`, `lot_seq = next per-tenant seq`). Postgres bumps `credit_lot_seq` (`INSERT … ON CONFLICT DO UPDATE
  SET next_seq = next_seq + 1 RETURNING next_seq`) in the same tx; InMemory mirrors with a `List<CreditLot>` +
  `NextLotSeq++` under the existing `Gate`. **Tests (both twins)**: lot minted on insert (`Σ per-source remaining ==
  balance`), no second lot on a deduped grant, multi-source Σ==balance, expired-Promo exclusion.

## Group 3 — FIFO metered debit (the money-path rewrite)

- [x] 3.1 `PostMeteredDebitAsync` (Postgres): inside the existing tx (projection row `FOR UPDATE` first), `SELECT …
  FROM credit_lot WHERE tenant_id=@T AND remaining > 0 AND (expires_at IS NULL OR expires_at > @Now) ORDER BY
  billable_priority(source) ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC FOR UPDATE`; walk lots,
  `draw = min(lot.remaining, outstanding)`, guarded `UPDATE credit_lot SET remaining = remaining - @Draw WHERE lot_id=@L
  AND remaining >= @Draw`, insert one source-tagged covered debit row + one `credit_allocation` row, decrement the
  projection by Σ draws; the uncovered remainder → exactly one `PostPaid` tail row (no lot, no allocation, projection
  untouched). Return `MeteredDebitResult(newBalance, Σcovered, tail)`. `billable_priority` is a SQL `CASE` or a joined
  static map — never `ORDER BY source`.
- [x] 3.2 `PostMeteredDebitAsync` (InMemory): mirror byte-for-byte — same total ordering (`OrderBy` priority, then
  `expires_at` with nulls-last, then `granted_at`, then `lot_seq`), same per-lot draw + allocation list, single PostPaid
  tail. n=1 degenerates to the current covered+tail.
- [x] 3.3 Make `PostBackfillConsumptionAsync` **lot-aware** (both stores): the covered portion draws from lots via the
  same FIFO so a back-fill can't drop the projection without decrementing a lot. (Keeps the `LedgerEnforcementEnabled`
  refusal guard.)
- [x] 3.4 Tests: multi-lot span (promo→sub→PostPaid), n=1 byte-identity (re-run the (a)/(b) numbers), no-open-lots pure
  tail, concurrent-no-overdraw, `credit_allocation` invisible to `GetEntries*`, invariant after every debit. Both twins.

## Group 4 — Lot-expiry reclaim sweeper

> Implemented as the GENERAL sweeper per ADR-0033 BLOCKER-2 + the spec "Lot expiry reclaims unconsumed credits
> idempotently" requirement: it reclaims ANY expired lot (Promo operator-expiry AND the period-end Subscription
> lot) and tags the offset with the lot's OWN source — enforcing subscription no-carryover and promo expiry in one
> mechanism. Worker is `CreditLotExpiryReclaimWorker`, marker `external_ref="lot-expiry:{lotId}"` (the spec form),
> superseding the earlier draft `PromoExpiryReclaimWorker`/`promo-expiry:` names.

- [x] 4.1 `ReclaimExpiredLotAsync` (both stores): one tx, projection row locked first; insert offsetting debit tagged
  the lot's own source `= lot.remaining` (read `FOR UPDATE`) with `external_ref="lot-expiry:{lotId}"`
  `ON CONFLICT DO NOTHING`; only if `inserted == 1` decrement projection by `remaining` + set lot `remaining = 0`.
  Idempotent + can't reclaim consumed. Plus `GetExpiredLotsAsync(now)` cross-tenant work-list (both stores).
- [x] 4.2 `CreditLotExpiryReclaimWorker : BackgroundService` (Billing) — copies the `CreditGrantMintWorker` shape exactly
  (`ResiliencePolicyKey`, `IServiceScopeFactory`, `IClock`, `[LoggerMessage]`, catch ordering, `internal
  ProcessExpiryCycleAsync`); per cycle enumerate expired non-zero lots and reclaim each (per-lot try/catch skip).
  Registered one `AddHostedService<CreditLotExpiryReclaimWorker>()` (Program.cs:560) + one keyed
  `AddKeyedSingleton<ResiliencePolicy>(…BuildHourlyWorkerPolicy())` (Program.cs:925-927).
- [x] 4.3 Tests: expiry reclaims only `remaining`, re-run is a no-op, expired lot is FIFO-skipped, partial-consume then
  expire reclaims the rest, no-carryover Subscription reclaim, cross-tenant sweep, Σ-by-source invariant.
  `ProcessExpiryCycleAsync` driven directly with a fixed `IClock`. InMemory (7) + Billing worker (4) + Postgres (4).

## Group 5 — Partner attribution (derive-on-read) + per-source reporting

- [x] 5.1 `GetPartnerAttributionAsync(partnerTenantId, periodStart, periodEnd, ct)` — resolve the partner's direct
  `Customer` children via `ITenantStore.GetChildrenAsync`, sum `|Partner-source debits|` per child in the half-open
  window. Lives in Billing over `ICreditLedgerStore` + `ITenantStore`. Gate: assert `parent.Type == TenantType.Partner`
  (reuse the `ManagementTenantEndpoints.cs:104` pattern).
- [x] 5.2 `GetRemainingBySourceAsync` (both stores): per-source open `remaining`, exclude expired Promo + PostPaid;
  `Σ == balance` test.

## Group 6 — API endpoints + DTOs (AOT)

- [x] 6.1 `CreditLedgerEndpoints`: add `POST /management/credit-ledger/promo-grant` and `… /partner-grant` mirroring the
  c1 top-up double-lock (`PlatformAdminOnly` group + `.RequireAuthorization(GrantPolicy)`), building `CreditLedgerEntry`
  with `Source = Promo|Partner`, `ExpiresAt` (promo), `ExternalRef = IdempotencyKey`; validate Amount>0, non-blank
  TenantId/key; Partner-grant validates the target tenant's parent is a Partner. Add `GET
  /admin/credit-ledger/remaining-by-source` (AdminOnly + `billing:credits:read`). New DTOs (`PromoGrantRequest`,
  `PartnerGrantRequest`, `SourceRemainingDto`, partner-attribution read DTO) registered in `ApiJsonContext`.
- [x] 6.2 Endpoint/handler tests + `ApiJsonContext` round-trip; confirm no reflection-based serialization.

## Group 7 — Verification + ship

- [ ] 7.1 Build 0 warnings (TreatWarningsAsErrors); full suite green (Billing + InMemory carry the behavioral matrix);
  **AOT native publish** clean (0 IL2026/IL3050/IL207x, ELF, 0 managed Verbara DLLs); final adversarial review (SHIP);
  PR → enqueue (GraphQL) → merge → `openspec archive credit-ledger-lots`. Web per-source widget = separate Platform.Web
  PR (out of scope).
