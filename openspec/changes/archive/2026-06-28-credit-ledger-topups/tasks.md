# Tasks — credit-ledger-topups (c1, ADR-0033 (c) addendum 2026-06-27)

> Re-grounded against post-(b) code. FCM: Phase A foundation (batch), Phase B endpoints+RBAC (focused),
> Phase C integration (batch). Native AOT, `TreatWarningsAsErrors`/`WarningLevel 9999`, test naming
> `Method_ShouldExpected_WhenCondition`. NO money-path change (`PostMeteredDebitAsync` untouched).

## Phase A — Foundation (batch)

- [x] A1. `ICreditLedgerStore.GetEntriesCountAsync(TenantId tenantId, CancellationToken ct)` → `Task<int>`
  (XML doc: total ledger entry count for `PagedResult.TotalCount`). Postgres: `SELECT COUNT(*) FROM
  ai_credit_ledger WHERE tenant_id=@TenantId` via `ExecuteScalarAsync<long?> ?? 0L` cast to int (covered by
  `idx_ai_credit_ledger_tenant_created`). InMemory: `ledger.Entries.Count` under `Gate`.
- [x] A2. Deterministic `GetEntriesAsync` tiebreak: Postgres already `ORDER BY created_at DESC, entry_id DESC`;
  make the **InMemory** twin order by `(CreatedAt DESC, EntryId DESC)` instead of raw insertion-reverse so the
  twins agree at same-instant rows. Add/adjust a twin-parity test.

## Phase B — Endpoints + RBAC (focused)

- [x] B1. **RBAC seeds** (`Storage.Postgres/Seeds/`): add `billing:credits:read` and `billing:credits:grant`
  to `PermissionSeeder.GetPermissions()` (category `billing`, resource `credits`, actions `read`/`grant`).
  `:read` → `RoleTemplateSeeder.AllPermissions()` (reaches platform_admin/admin/system_admin — tenant reads own
  balance, OK). `:grant` → **NOT** `AllPermissions()`; add directly to the `platform_admin` template and the
  hand-listed `partner_admin` array only. Update any permission-count / role-template-count assertions in the
  RBAC tests. Note in the change: `RbacReseed` reseeds only `platform_admin` on existing tenants.
- [x] B2. **DTOs + `ApiJsonContext`**: `internal sealed record TopUpRequest(string TenantId, decimal Amount,
  string IdempotencyKey)`; `CreditBalanceResponse(decimal Balance)`; `CreditLedgerEntryDto(string EntryId,
  string EntryType, string Source, decimal Amount, string? ExternalRef, DateTimeOffset? ExpiresAt,
  DateTimeOffset CreatedAt)` (map `EntityId.Value`, enums via `.ToString()`); register all three +
  `PagedResult<CreditLedgerEntryDto>` in `ApiJsonContext`.
- [x] B3. **`CreditLedgerEndpoints`** (`Api/Endpoints/CreditLedgerEndpoints.cs`):
  - `POST /management/credit-ledger/top-up` group `RequireAuthorization("PlatformAdminOnly")`, route
    `.RequireAuthorization("billing:credits:grant")`: validate `Amount > 0` (400 typed `ErrorResponse`),
    build `new CreditLedgerEntry { EntryId = EntityId.New(), TenantId = new(req.TenantId), EntryType = Grant,
    Source = TopUp, Amount = req.Amount, ExternalRef = req.IdempotencyKey, CreatedAt = clock.UtcNow }`, call
    `PostGrantAsync` (idempotent), return `Results.Ok(new MessageResponse(...))` or the new balance.
  - `GET /admin/credit-ledger/balance` group `RequireAuthorization("AdminOnly").RequireOperationalTenant()`,
    route `.RequireAuthorization("billing:credits:read")`: tenant from `context.Items["TenantId"]`,
    `GetBalanceAsync` → `CreditBalanceResponse`.
  - `GET /admin/credit-ledger/entries` (same gating): `page`/`pageSize` query (clamp), `GetEntriesAsync` +
    `GetEntriesCountAsync` → `PagedResult<CreditLedgerEntryDto>`.
  - `IClock clock` injected for `CreatedAt`. Map `v1.MapCreditLedgerEndpoints()` in `Program.cs` near the
    AI-credits / management-billing maps.

## Phase C — Integration (batch)

- [x] C1. Tests: top-up adds balance + idempotent re-post no-op; unauthorised (no `:grant`) 403; non-positive
  amount 400; tenant balance read scoped to caller; paginated entries with accurate `TotalCount`/`TotalPages`;
  tenant `admin` cannot top-up (403); invoice unchanged for an allowance+top-up tenant (PostPaid still
  `Σ`-correct); the (b) characterization tests stay green (money-path untouched).
- [x] C2. Verify: `dotnet build` 0 warnings; full suite green; AOT publish gate clean; `openspec validate
  credit-ledger-topups --strict`. Web balance widget = separate Platform.Web PR (deferred).
