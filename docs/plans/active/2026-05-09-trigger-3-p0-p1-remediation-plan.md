# Trigger 3 P0 + P1 Remediation Plan (v1.13.x patch train)

**Created:** 2026-05-09
**Status:** Active (planning, not yet executed)
**Repo:** `Verbara.Platform`
**Origin:** Pre-public security review on 2026-05-09 (`docs/security/2026-05-09-pre-public-security-review.md`) — 2 P0 + 4 P1 findings block ADR-0018 Trigger 3 GREEN. Findings are grep-able from source the moment the repository flips public.

## Context

The deeper Trigger 3 audit (60 endpoints, 4 sensitive families) raised 10 findings. The 6 P0/P1 findings each have a documented exploit path that requires no sophisticated tooling — single header swap, single GET, single query parameter, missing audit emissions, or a 6-line bypass in `PlatformAdminAuthorizationHandler`. They must be remediated before the visibility flip per ADR-0018 §"Trigger checklist (must all be ✅ before flipping)" item 3.

This plan does **not** revisit the visibility decision (correct per ADR-0018) and does **not** address Trigger 5 (Pro tamper-resistance, separate research at `Verbara.Sdk.Pro/docs/research/2026-05-09-pro-image-binding-research.md`) or Trigger 7 (Tier 0.5 e2e validation, executed 2026-05-09, awaiting ADR-0018 status update for formal closure). It is a focused code-remediation track for the audit findings.

## Goal

By end of this plan: Platform v1.13.x patch train ships with all 2 P0 + 4 P1 findings closed, regression tests added, threat-model `Status updates` section refreshed with the new verified state, and ADR-0018 Trigger 3 flipped from ❌ BLOCKED to ✅ GREEN.

## Non-goals

- **Visibility flip itself** — gated by all 7 triggers, not just Trigger 3
- **Fixing the 3 P2 findings inline** (`ADMIN-003` Setup hard-coding, `MFA-002` MFA re-enroll step-up, `MT-002` webhook DLQ tenant trust) — these track as separate v1.13.x tickets, do not block flip per audit sign-off, and reduce review-load if scoped separately
- **Fixing the 3 prior PENDING findings** from the 2026-04 audit (`CFG-003` dev-config secrets, `MFA-007` in-memory cache default) — already tracked elsewhere, intentionally outside this plan's scope
- **Code refactors beyond what each fix requires** — minimum-diff to close the finding; no surrounding cleanup; per `superpowers:test-driven-development` discipline

## Phase ordering rationale

P0 fixes ship first because:
1. They are pre-conditions for any other code change merging without re-introducing the breach (e.g. fixing `BILL-002` while `MT-001` still allows cross-tenant header escalation does not actually contain the attack).
2. Each P0 has a smaller blast radius for review — easier to land in isolation.
3. If a P1 fix takes longer than expected, the P0 fixes are still ship-worthy on their own and can land as v1.13.0.

P1 fixes ship in parallel where independent. `BILL-001` (audit-emission gaps on 8 handlers) and `BILL-002` (`PayInvoice` trust) overlap and should land together. `MFA-001` (tenant-scoping bypass) is independent. `ADMIN-002` (management API key bypass) is independent but has the highest review-cost because it touches `PlatformAdminAuthorizationHandler` consumed by every `/management/*` endpoint.

---

## Phase 0 — Preparation (Wk 1, ~half day)

### 0.1 — Branch + CI baseline

- [ ] Create branch `release/v1.13.x-trigger3-remediation` from `main`
- [ ] Verify CI green on baseline: `dotnet test Verbara.Platform.slnx` passes locally
- [ ] Confirm `dotnet list package --vulnerable --include-transitive` clean (post-MailKit bump)
- [ ] Tag baseline commit with notes for rollback reference

### 0.2 — Test infrastructure

The audit-checklist Scope 2.1 amendment ("test against `AdminOnly` policies specifically") needs scaffolding. Add a shared test fixture in `tests/Verbara.Platform.Api.Tests/Multitenancy/` that authenticates as a fabricated tenant Admin and probes both:
- `/admin/users|queues|agents|teams|audit` (bare `AdminOnly`)
- `/management/mfa/users/*` (bare `MfaAdminGate`)
- A control: `/management/tenants/{id}` (`PlatformAdminOnly` — should pass for host, fail for non-host)

This fixture is reused by every regression test below. It also documents the audit-criterion gap formally.

- [ ] `tests/Verbara.Platform.Api.Tests/Multitenancy/CrossTenantHeaderAttackFixture.cs` — reusable fixture; one method per attack shape (header override, query override, `targetTenant` override)

**Phase 0 exit:** branch + CI baseline + reusable fixture in place. No production code change yet.

---

## Phase 1 — P0 fixes (Wk 1-2, parallel-merge target)

### 1.1 — Fix MT-001: Cross-tenant data access via `X-Tenant-Id` on `/admin/*`

**Owner:** `dotnet-aot-dapper-asterisk-expert` subagent
**Estimate:** ~3-4 h (middleware + tests + threat-model row update)
**Dependencies:** Phase 0 fixture
**Affected:**
- New file `src/Verbara.Platform.Api/Middleware/TenantBoundaryValidationMiddleware.cs`
- `src/Verbara.Platform.Api/Program.cs:1219+` — middleware registration order (between `UseAuthorization` and endpoint pipeline)

**Approach:** New middleware rejects any request whose `context.Items["TenantId"]` (from `X-Tenant-Id` header / subdomain via `TenantResolutionMiddleware`) does not match the authenticated principal's `tid` claim, **unless** the principal is a `key_type=management` API key OR the caller resolves to a `TenantType.Platform` or `TenantType.Partner` tenant. This preserves the legitimate cross-tenant access patterns (Platform-admin and Partner-admin operating on customer tenants) while closing the bare `AdminOnly` surface.

Pseudo per audit recommendation:
```csharp
public async Task InvokeAsync(HttpContext context)
{
    if (context.User?.Identity?.IsAuthenticated != true) { await _next(context); return; }
    if (context.User.FindFirst("key_type")?.Value == "management") { await _next(context); return; }

    var jwtTid = context.User.FindFirst("tid")?.Value
              ?? context.User.FindFirst("tenant_id")?.Value;
    if (!context.Items.TryGetValue("TenantId", out var v) || v is not TenantId resolved
        || jwtTid is null
        || string.Equals(jwtTid, resolved.Value, StringComparison.OrdinalIgnoreCase))
    { await _next(context); return; }

    var store = context.RequestServices.GetRequiredService<ITenantStore>();
    var callerTenant = await store.GetAsync(jwtTid);
    if (callerTenant?.Type is TenantType.Platform or TenantType.Partner) { await _next(context); return; }

    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    await context.Response.WriteAsJsonAsync(
        new ErrorResponse("Tenant header does not match authenticated principal."),
        ApiJsonContext.Default.ErrorResponse);
}
```

**Tests required:**
- [ ] `TenantBoundary_ShouldReturn403_WhenAdminCallsForeignTenantViaHeader_OnAdminUsers` (and three sister tests for `queues`, `agents`, `audit`)
- [ ] `TenantBoundary_ShouldAllow_WhenPlatformAdminUsesHeader` (regression-guard for legitimate cross-tenant access)
- [ ] `TenantBoundary_ShouldAllow_WhenPartnerAdminTargetsOwnCustomer` (legitimate Partner access)
- [ ] `TenantBoundary_ShouldAllow_WhenManagementApiKeyUsed` (management API key bypass intentional — pinned to `ADMIN-002` for follow-up)
- [ ] `TenantBoundary_ShouldAllow_WhenNoHeaderPresent_AndJwtTidUsed` (default path unaffected)

**Acceptance:** Phase 0 fixture's foreign-tenant probes return 403 for Customer-tenant Admins; legitimate Platform/Partner cross-tenant flows continue to return 200; no regression in the existing `Verbara.Platform.Api.Tests` suite.

### 1.2 — Fix ADMIN-001: OIDC client secret persisted + returned plaintext

**Owner:** `dotnet-aot-dapper-asterisk-expert` subagent (separate task; touches Storage.Postgres + Identity + Api)
**Estimate:** ~3-4 h (Protect/Unprotect + DTO redaction + migration + tests)
**Dependencies:** none (independent of MT-001)
**Affected:**
- `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs:30-77, 96, 120` (Protect on write, Unprotect on read)
- `src/Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs:33-37` (project to redacted DTO)
- `src/Verbara.Platform.Identity/TenantAuthConfig.cs` — separate response DTO `TenantAuthConfigResponse` with `OidcClientSecretSet: bool` + `OidcClientSecretFingerprint: string` (first 8 hex of SHA-256), no raw value
- `src/Verbara.Platform.Api/Endpoints/OidcEndpoints.cs:140` — verify token exchange consumes Unprotected value
- New migration `XXX_EncryptOidcClientSecret.sql` — one-shot wrap of existing rows during deploy
- `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — register new DTO

**Approach:** Inject `IDataProtectionProvider`, create a named protector `"Verbara.OidcClientSecret"` (matches the convention from `Verbara.Sdk.Pro` for inter-package consistency). Wrap on write; unwrap on read for the OIDC token-exchange code path; never expose the raw value through any HTTP surface. The migration must be idempotent (skip already-encrypted rows; detect via try-Unprotect catch).

Per audit, also extend ADR-0003 to make column-level secret persistence MUST go through `IDataProtectionProvider`. That ADR amendment lands in the same commit as the code fix.

**Tests required:**
- [ ] `Save_ShouldPersistEncrypted_WhenOidcClientSecretProvided` — assert raw row value differs from input
- [ ] `Get_ShouldReturnOriginalSecret_WhenStoreUsedInternally` — round-trip Protect/Unprotect
- [ ] `GetConfig_ShouldReturnFingerprintOnly_WhenOidcClientSecretSet` — API response never carries raw value
- [ ] `GetConfig_ShouldReturnFalse_WhenOidcClientSecretAbsent` — `OidcClientSecretSet: false`
- [ ] `OidcTokenExchange_ShouldSucceed_WhenStoredSecretIsEncrypted` — end-to-end IdP flow continues to work
- [ ] `Migration_ShouldEncryptExistingRows_AndBeIdempotent` — runs cleanly on fresh schema and on already-migrated schema

**Acceptance:** New row inserts produce ciphertext in DB; HTTP response does not contain the secret value; existing OIDC flow still completes against a real IdP (Testcontainers-based test if feasible, otherwise stubbed `IdpClient`).

### 1.3 — Threat-model row updates (post-fix)

After 1.1 + 1.2 merge:
- [ ] Append a **2026-05-XX** Status update entry to `docs/security/threat-model.md` flipping the §6.2 Tampering row from "factually superseded" to ✅ Verified-fixed (cite the new middleware + tests)
- [ ] Add an §6.4 Information-Disclosure row for OIDC secret protection: ✅ Verified

**Phase 1 exit:** Both P0 findings closed in code, tests in CI, threat-model updated. Branch carries 4-6 commits (middleware + tests, OIDC store + DTO + migration + tests + ADR amend, threat-model updates).

---

## Phase 2 — P1 fixes (Wk 2-3, parallel where independent)

### 2.1 — Fix MFA-001: `?targetTenant=` accepts any tenant id

**Owner:** `dotnet-aot-dapper-asterisk-expert` subagent
**Estimate:** ~2 h (async resolver + tests)
**Dependencies:** none
**Affected:**
- `src/Verbara.Platform.Api/Endpoints/Mfa/MfaAdminEndpoints.cs:55, 59, 85, 89, 128-131` — replace `ResolveTargetTenant` (sync) with `ResolveTargetTenantAsync` (async, validates hierarchy)
- New audit category constant `AuthEventTypes.MfaPrivilegeEscalationAttempted`

**Approach:** Mirror the impersonation hierarchy pattern (`ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync` at line 286). If `overrideTenant` is null/whitespace → use actor's tenant. If equals actor's tenant → use it. If actor is `TenantType.Platform` → allow any. Else → walk hierarchy; `403` + audit-emit `MfaPrivilegeEscalationAttempted` if not in hierarchy.

**Tests required:**
- [ ] `ResetMfa_ShouldReturn403_WhenPartnerCrossesIntoForeignHierarchy`
- [ ] `ResetMfa_ShouldAllow_WhenPartnerTargetsOwnChild`
- [ ] `ResetMfa_ShouldAllow_WhenPlatformAdminTargetsAny`
- [ ] `RevokeSessions_ShouldReturn403_WhenPartnerCrossesIntoForeignHierarchy` (sister test for the second handler)

### 2.2 — Fix BILL-001 + BILL-002: Billing audit emissions + `PayInvoice` trust

These are bundled because they touch the same file (`ManagementBillingEndpoints.cs`) and share the audit-emission machinery. Splitting would cause merge thrash.

**Owner:** `dotnet-aot-dapper-asterisk-expert` subagent (single task, both findings)
**Estimate:** ~4-5 h (8 handlers × emit + `PayInvoice` cross-check + tests)
**Dependencies:** none (but if 1.1 lands first, the cross-tenant aspect of BILL-002 is partially mitigated)
**Affected:**
- `src/Verbara.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` — 8 handlers (`CreateRateCard`, `UpdateRateCard`, `DeleteRateCard`, `GenerateInvoice`, `IssueInvoice`, `PayInvoice`, `UpdateQuota`, `PauseDunning`)
- `src/Verbara.Platform.Audit/AuditEventTypes.cs` (or equivalent) — add `BillingRateCardCreated`, `BillingRateCardUpdated`, `BillingRateCardDeleted`, `BillingInvoiceGenerated`, `BillingInvoiceIssued`, `BillingInvoicePaid`, `BillingTenantQuotaUpdated`, `BillingDunningPaused` constants

**Approach for BILL-001:** Inject `[FromServices] IAuditService audit` + `HttpContext context` into each handler. Emit before the response. Use the same shape `ResumeDunning` already follows. Critical: severity `"warning"` for create/update/issue/pay (financial impact, normal flow); `"critical"` for delete (potentially destructive). Each entry MUST include actor, target id, and `Before/After` change set via `AuditChanges(...)`. `PayInvoice` specifically must record `payment_status_before/after` AND `tenant_status_before/after` (both flip in this handler).

**Approach for BILL-002:** Add `?tenantId=` query parameter to `PayInvoice`; require it; assert `delivery.TenantId == queryTenantId` after the dunning-record lookup; return `400 Bad Request` if mismatch (with audit-emit `billing.invoice.pay_attempted` severity `"warning"`). Validate `invoiceId` shape via `EntityId.IsValid` before the store call; return `400` if not valid.

**Tests required:**
- [ ] One audit-assertion test per handler — `Create_ShouldEmitAudit_WhenSucceeds` × 8
- [ ] `PayInvoice_ShouldRecordAudit_WhenSucceeds` (overlap with above; explicit because of the dual-status emission)
- [ ] `PayInvoice_ShouldReject400_WhenTenantIdMismatchesDunningRecord`
- [ ] `PayInvoice_ShouldReject400_WhenInvoiceIdNotValid`

### 2.3 — Fix ADMIN-002: Management API key bypasses every permission check

**Owner:** `dotnet-aot-dapper-asterisk-expert` subagent (highest review-cost; coordinator should review before merging)
**Estimate:** ~3 h (handler logic + scope-aware key issuance + tests + ADR addendum)
**Dependencies:** ideally lands AFTER 1.1 (MT-001 fix) so the regression-test surface is stable
**Affected:**
- `src/Verbara.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs:27-32` — replace short-circuit with scope-aware permission check
- `src/Verbara.Platform.Api/Endpoints/ManagementApiKeyEndpoints.cs:68` — change `scopes=["platform:*"]` blanket to require explicit permission whitelist at issuance
- New ADR `docs/decisions/0019-scope-aware-management-api-keys.md` documenting the model change (this *is* a load-bearing decision that affects existing customer integrations)

**Approach:** Replace the `if (keyTypeClaim == "management") { context.Succeed(requirement); return; }` short-circuit with: read the API key's `scopes` array from claims, check whether the requested `requirement.Permission` is contained, succeed iff yes. Existing API keys with `scopes=["platform:*"]` keep working via wildcard expansion (back-compat) — but new keys default to a minimum-scope whitelist.

The ADR addendum explicitly documents this is a back-compat change for new keys. Existing `platform:*` keys remain valid through v1.13.x; v1.14.x ships a deprecation warning; v1.15.x removes the wildcard.

**Tests required:**
- [ ] `MgmtKey_ShouldSucceed_WhenScopeIncludesPermission`
- [ ] `MgmtKey_ShouldFail_WhenScopeDoesNotIncludePermission`
- [ ] `MgmtKey_LegacyWildcardScope_ShouldSucceed` (back-compat regression-guard)
- [ ] `CreateApiKey_ShouldRequireExplicitScopes_WhenNotLegacy`
- [ ] `UseMgmtKey_ShouldEmitAudit_WhenReachesPermissionGatedSurface_AndPermissionAbsent` (the audit-emission startup-warn; alternative: integration test against `JwtKeyEndpoints.RotateKey` which is the canary for "high-value gate")

**Acceptance:** Permission-gated surfaces (`security.jwt.rotate`, `audit.export`, `retention.manage`) are no longer reachable by a wildcard management API key without that specific permission in scope; existing customer integrations using legacy wildcard keys continue to work; new key issuance defaults to least-privilege.

### 2.4 — Threat-model row updates (post-fix)

After all P1 fixes merge:
- [ ] Append Status update flipping §6.6 Elevation-of-privilege rows for MFA admin tenant-scoping (currently absent — add as new row) and management API key permission enforcement (currently implicit; make explicit)
- [ ] Append Status update marking §6.3 Repudiation row for billing operations now ✅ (was ✅ Verified by sample, sample missed billing — flip to ✅ Verified-comprehensive after BILL-001 lands)

**Phase 2 exit:** All 4 P1 findings closed; threat-model reflects the new verified state; CI green.

---

## Phase 3 — Re-audit + sign-off (Wk 3, ~half day)

### 3.1 — Re-audit pass

- [ ] Spawn the same `general-purpose` subagent with the same prompt as the original Trigger 3 audit, focused only on the 6 fixed findings — confirm each is closed with the recommended approach
- [ ] Confirm no new findings introduced by the fixes (defense-in-depth regression check)
- [ ] Append an "Audit follow-up" section to the original `2026-05-09-pre-public-security-review.md` with the re-audit verdict per finding

### 3.2 — ADR-0018 status update

- [ ] Append to `docs/decisions/0018-visibility-decision-3-private-now-public-on-trigger.md` "Status update" section: Trigger 3 ✅ GREEN as of `<merge-date>`; reference re-audit pass; do NOT modify the original ADR text per append-only convention

### 3.3 — Update visibility plan (this repo's main visibility plan)

- [ ] Update `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md` Trigger 3 section: flip from ❌ BLOCKED to ✅ GREEN; reference this remediation plan as completed

**Phase 3 exit:** Trigger 3 formally GREEN with audit + ADR + plan all aligned.

---

## Phase 4 — Release v1.13.0 (Wk 3-4)

### 4.1 — Release prep

- [ ] CHANGELOG.md entry: "Security: 2 P0 + 4 P1 findings closed (PREPUB-2026-05-09 series). See `docs/security/2026-05-09-pre-public-security-review.md` for details."
- [ ] Bump version to `v1.13.0` in `Directory.Build.props`
- [ ] `dotnet pack -c Release` clean across all projects

### 4.2 — Release tag + GitHub release

- [ ] Tag `v1.13.0` on `main` after PR merge
- [ ] Create GitHub release with security advisory note (private repo today; published when visibility flip occurs)

### 4.3 — Plan completion

- [ ] `git mv docs/plans/active/2026-05-09-trigger-3-p0-p1-remediation-plan.md docs/plans/completed/`

**Phase 4 exit:** v1.13.0 shipped, plan archived to completed/.

---

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| MT-001 fix breaks legitimate Platform/Partner cross-tenant flows | Phase 0 fixture explicitly tests both attack and legitimate-use; tests must pass before merge |
| OIDC migration encrypts secrets twice on re-run | Migration is idempotent: try-Unprotect, skip if already encrypted (catches `CryptographicException`) |
| ADMIN-002 fix breaks existing customer integrations using legacy wildcard `platform:*` keys | Wildcard kept working through v1.13.x via back-compat; deprecation warning v1.14.x; removal v1.15.x. ADR-0019 documents the migration path. |
| Audit-emission additions in BILL-001 introduce performance regression on hot billing paths | `IAuditService.AppendAsync` is async + non-blocking; benchmark before/after the change; if regression > 5%, defer to async-fire-and-forget per audit-checklist Scope 4.4 |
| One subagent task overruns budget (e.g. ADMIN-002 turns out to need broader DI changes) | Task is independently mergeable; if blocked, P0 fixes (1.1 + 1.2) ship first as v1.13.0 and ADMIN-002 lands in v1.13.1 |
| Threat-model status-updates accumulate (3 separate updates from this plan: 1.3, 2.4, 3.2) | Acceptable — threat-model is append-only by design; each update is small and dated |

## Dependencies

- **`Verbara.Platform.Identity`** — `TenantAuthConfig` DTO refactor for ADMIN-001 fix touches public surface (consumer of Storage.Postgres)
- **`Verbara.Platform.Audit`** — new `AuthEventTypes.MfaPrivilegeEscalationAttempted` constant + 8 new billing event constants
- **No SDK or Pro changes** — all fixes are Platform-internal

## Cross-references

- Audit findings: `docs/security/2026-05-09-pre-public-security-review.md`
- Threat model: `docs/security/threat-model.md` (will receive 3 Status updates as fixes land)
- Visibility plan (parent): `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md`
- ADR-0018 (Trigger 3 source): `docs/decisions/0018-visibility-decision-3-private-now-public-on-trigger.md`
- Audit checklist (severity definitions + Scope.Item taxonomy): `docs/security/audit-checklist.md`
- Prior audit (2026-04): `docs/security/internal-audit-2026-04.md` (3 still-PENDING findings outside this plan's scope)
- Pro tamper-resistance research (Trigger 5, parallel track): `Verbara.Sdk.Pro/docs/research/2026-05-09-pro-image-binding-research.md`
