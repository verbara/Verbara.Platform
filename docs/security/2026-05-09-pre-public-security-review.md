# Pre-Public Security Review — 2026-05-09 (ADR-0018 Trigger 3)

**Scope:** ADR-0018 Trigger 3 pre-public flip
**Date:** 2026-05-09
**Method:** Code-review-based audit, focused on 4 sensitive endpoint families per `audit-checklist.md`. Layered review of authn → authz → tenant scoping → input validation → output filtering → audit log on each shipped handler. Pre-existing v1.13.x tickets (AUTH-002, CFG-003, MFA-007) cross-referenced rather than re-scoped; AUTH-002 confirmed CLOSED in v1.14.4.
**Auditor:** Pre-public visibility flip review subagent

## Summary

| Family | Endpoints reviewed | P0 | P1 | P2 | P3 | Total |
|---|---|---:|---:|---:|---:|---:|
| Billing (`/management/{rate-cards,invoices,tenants/*}`, `/partner/*`) | 18 | 0 | 2 | 1 | 0 | 3 |
| Multi-tenant boundary (`/admin/*`, `/admin/audit`, settings) | sample 8 of `/admin/*` + audit query | 1 | 0 | 1 | 0 | 2 |
| Admin operations (`/management/*`, `/admin/auth/*`) | 24 | 1 | 1 | 1 | 0 | 3 |
| MFA enforcement (`/auth/mfa/*`, `/profile/security/mfa/*`, `/management/mfa/*`) | 10 | 0 | 1 | 1 | 0 | 2 |
| **Total** | **60** | **2** | **4** | **4** | **0** | **10** |

**Trigger 3 status: BLOCKED.** Two P0 findings expose cross-tenant data and a long-lived plaintext OAuth secret on a path reachable by any tenant Admin. Four P1 findings (one tenant-scoping bypass on MFA admin, three audit-emission gaps on billing mutations) must be closed before flip — public source enables an attacker to read the bypass code paths directly. P2 findings track as v2.0.x patch tickets.

---

## Endpoints reviewed (catalog)

| Method | Path | Handler | Auth gate | Tenant-scoping mechanism |
|---|---|---|---|---|
| `POST` | `/api/setup` | `SetupEndpoints.Setup` (`SetupEndpoints.cs:16`) | `AllowAnonymous` (host-not-yet-initialized guard) | Constant `"platform"` |
| `POST` | `/management/impersonate` | `ManagementImpersonationEndpoints.StartImpersonation` (`ManagementImpersonationEndpoints.cs:67`) | `PlatformAdminOnly` + permission `platform:tenant:impersonate` | Hierarchy walk via `IsTenantInCallerHierarchyAsync` |
| `DELETE` | `/management/impersonate` | `EndImpersonation` (`ManagementImpersonationEndpoints.cs:68`) | `PlatformAdminOnly` | JWT `impersonator_tenant` |
| `GET` | `/management/impersonation/sessions/active` | `ListActiveSessions` (`ManagementImpersonationEndpoints.cs:75`) | `ImpersonationAdminGate` | `ResolveAdminScopeAsync` (Platform → any, Partner → own) |
| `POST` | `/management/impersonation/sessions/{id}/revoke` | `RevokeSession` (`ManagementImpersonationEndpoints.cs:76`) | `ImpersonationAdminGate` | Same; cross-actor-tenant denied for partner |
| `GET` | `/management/impersonation/sessions/history` | `ListSessionHistory` (`ManagementImpersonationEndpoints.cs:77`) | `ImpersonationAdminGate` | Same |
| `GET/POST/PUT/DELETE` | `/management/tenants[/{id}[/...]]` (7 handlers) | `ManagementTenantEndpoints` (`ManagementTenantEndpoints.cs:15-23`) | `PlatformAdminOnly` | JWT `tid` ownership check on `CreateTenant`; path tenant-id passthrough on rest |
| `GET/POST/PUT/DELETE` | `/management/api-keys` (4) | `ManagementApiKeyEndpoints` (`ManagementApiKeyEndpoints.cs:15-20`) | `PlatformAdminOnly` | Pinned to host tenant (`GetHostTenantAsync`) |
| `GET/POST` | `/management/system/{info,license,settings}` (5) | `ManagementSystemEndpoints` (`ManagementSystemEndpoints.cs:34-40`) | `PlatformAdminOnly` | Host tenant only |
| `GET/POST/PUT/DELETE` | `/management/cluster/...` (10) | `ManagementClusterEndpoints` (`ManagementClusterEndpoints.cs:23-35`) | `PlatformAdminOnly` | None (cluster-global) |
| `GET/POST` | `/management/webhooks/dead-letter[/{id}/retry]` (2) | `ManagementWebhookEndpoints` (`ManagementWebhookEndpoints.cs:12-14`) | `PlatformAdminOnly` | Required `?tenantId=` query (no JWT cross-check) |
| `GET/PUT` | `/management/tenants/{id}/settings` (2) | `ManagementTenantSettingsEndpoints` (`ManagementTenantSettingsEndpoints.cs:25`) | `PlatformAdminOnly` | Path `id` |
| `GET/PUT` | `/management/tenants/{tenantId}/ip-allowlist` | `ManagementTenantIpAllowlistEndpoints` (`...IpAllowlistEndpoints.cs:28`) | `PlatformAdminOnly` | Path `tenantId` |
| `POST` | `/management/security/jwt/rotate-key` | `JwtKeyEndpoints.RotateKey` (`Security/JwtKeyEndpoints.cs:39`) | `JwtKeyRotationGate` (`security.jwt.rotate`) | Host tenant only |
| `GET` | `/management/security/jwt/keys` | `JwtKeyEndpoints.ListKeys` (`Security/JwtKeyEndpoints.cs:40`) | Same | Host tenant only |
| `GET/POST/PATCH` | `/management/retention/{targets,config,run-now}` | `RetentionAdminEndpoints` (`Retention/RetentionAdminEndpoints.cs:35-38`) | `RetentionReadGate` / `RetentionManageGate` | Global config (host-tenant attribution) |
| `GET/POST` | `/management/mfa/{users,users/{id}/reset,users/{id}/sessions/revoke}` | `MfaAdminEndpoints` (`Mfa/MfaAdminEndpoints.cs:24-26`) | `MfaAdminGate` (`security.mfa.admin`) | `ResolveTargetTenant(actor.TenantId, ?targetTenant=)` — **no validation** |
| `GET/POST/PUT/DELETE` | `/management/rate-cards`, `/management/invoices`, `/management/tenants/{id}/{dunning,usage,quota}` (15) | `ManagementBillingEndpoints` (`ManagementBillingEndpoints.cs:14-43`) | `PlatformAdminOnly` | `?tenantId=` query OR path `{id}` (no JWT cross-check) |
| `GET/POST/PUT/DELETE` | `/partner/rate-cards`, `/partner/customers/{customerId}/{invoices,usage}` (7) | `PartnerBillingEndpoints` (`PartnerBillingEndpoints.cs:13-32`) | `PartnerAdminOnly` + `partner:billing:{view,manage}` | `customer.ParentTenantId == callerTenantId` ownership check |
| `GET/POST/PUT/DELETE` | `/partner/customers[/{customerId}/...]` (8) | `PartnerCustomerEndpoints` (`PartnerCustomerEndpoints.cs:18-37`) | `PartnerAdminOnly` + `partner:customer:*` | Same ownership check |
| `GET/POST/PUT/DELETE` | `/admin/{users,queues,agents,teams,queue-members}` (~20) | `AdminEndpoints` (`AdminEndpoints.cs:15`) | `AdminOnly` (`RequireRole("Admin")`) | `context.Items["TenantId"]` (X-Tenant-Id / subdomain) |
| `GET/PUT/DELETE` | `/admin/auth/{config,events,sessions[...]}` (6) | `AuthAdminEndpoints` (`AuthAdminEndpoints.cs:13`) | `AdminOnly` | JWT `tid` claim (`GetTenantId` reads claim, NOT items) |
| `GET` | `/admin/audit/{,{entityType}/{entityId}}` (2) | `AuditEndpoints` (`AuditEndpoints.cs:11`) | `AdminOnly` | `context.Items["TenantId"]` |
| `GET` | `/admin/audit/{events,export}` (2) | `AuditAdminEndpoints` (`Audit/AuditAdminEndpoints.cs:51-52`) | `AuditAdminGate` / `AuditAdminExportGate` | `ResolveTenantScope` (claim + role-gated `?tenantId` override) |
| `POST` | `/auth/mfa/{setup,confirm,verify}`, `DELETE /auth/mfa`, `POST /auth/mfa/recovery-codes/regenerate` (5) | `AuthEndpoints` (`AuthEndpoints.cs:38-50`) | `RequireAuthorization()` (verify is anonymous w/ pending token) | JWT claim |
| `POST` | `/profile/security/mfa/enroll/{init,verify,complete}` (3) | `MfaEnrollEndpoints` (`Profile/MfaEnrollEndpoints.cs:48-50`) | `RequireAuthorization()` | JWT claim |

Note: SignalR hub `/hubs/*` and inbound channel webhooks `/webhooks/{tenantId}/{channel}` are out of Trigger 3 scope; reviewed only for AUTH-002 confirmation (closed v1.14.4 per `Auth/AuthSchemeConfiguration.cs:54-62`).

---

## Findings

### PREPUB-2026-05-09-MT-001 — Cross-tenant data access via `X-Tenant-Id` header on `/admin/*` and legacy `/admin/audit` (P0, Scope 2.1 + 2.4)

**Severity:** P0 (active cross-tenant leak; trivially exploitable by any authenticated tenant Admin)
**Scope:** Scope 2.1 (read endpoints filter by `ITenantContext`) + Scope 2.4 (audit query tenant scoping) + Scope 1.2 (broken access control on tenant-admin)
**Affected:**
- `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs:75-103` (resolves `TenantId` from header/subdomain BEFORE auth runs)
- `src/Verbara.Platform.Api/Auth/AuthSchemeConfiguration.cs:86-99` (`OnTokenValidated` only sets `TenantId` if not already present — header wins)
- `src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs:589-595` (`GetTenantId` reads `context.Items["TenantId"]`, never JWT)
- `src/Verbara.Platform.Api/Endpoints/AuditEndpoints.cs:71-77` (legacy `/admin/audit` — same)
- `src/Verbara.Platform.Api/Program.cs:1217-1219` (middleware order: `TenantResolutionMiddleware` → `UseAuthentication` → `UseAuthorization`)

**Observation:** The `AdminOnly` policy is bare `RequireRole("Admin")` (`Program.cs:960`). It does not pin the caller to their own tenant. `TenantResolutionMiddleware.ResolveTenantIdAsync` (line 75) populates `context.Items["TenantId"]` from `X-Tenant-Id` header / subdomain BEFORE authentication runs. `JwtBearerEvents.OnTokenValidated` (line 90) only sets `TenantId` "if middleware didn't already resolve one":

```csharp
// AuthSchemeConfiguration.cs:90-95
if (!context.HttpContext.Items.ContainsKey("TenantId"))
{
    var tenantClaim = context.Principal?.FindFirst("tid")?.Value;
    if (tenantClaim is not null)
        context.HttpContext.Items["TenantId"] = new TenantId(tenantClaim);
}
```

`AdminEndpoints.GetTenantId` then trusts `context.Items["TenantId"]` unconditionally:

```csharp
// AdminEndpoints.cs:589-595
private static TenantId GetTenantId(HttpContext context)
{
    if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
        return tid;
    throw new InvalidOperationException("Tenant ID not resolved");
}
```

**Repro:** A tenant `acme` Admin logs in (JWT `tid=acme`), then issues:
```http
GET /api/v1/admin/users HTTP/1.1
Authorization: Bearer <valid-jwt-for-acme>
X-Tenant-Id: victim-tenant
```
The handler resolves tenant `victim-tenant` from `context.Items["TenantId"]`, calls `IUserStore.ListAsync(victim-tenant, ...)`, and returns the victim's users (and queues, agents, teams via the same pattern). Same exposure on `GET /admin/audit/...` — the legacy audit search returns the victim tenant's audit log to a foreign tenant Admin.

**Risk:** Direct violation of the canonical multi-tenant boundary documented in [ADR-0002](../decisions/0002-tenant-stamping-pipeline-end-to-end.md) and the threat-model row "TA1 modifies request to access cross-tenant resource via path traversal" (which currently lists this as ✅ Verified — the verification was based on the management surface where `PlatformAdminOnly` adds the host/partner gate; the bare `AdminOnly` surfaces were never re-checked). Once this repository goes public, the bypass is grep-able from the source: any external attacker who acquires *any* tenant's Admin credential reads every other tenant's user database, queue config, agent extensions/SIP passwords (`AdminEndpoints.cs:431-433` returns `Extension` and `SipPassword` in `Agent` DTO), and audit history.

**Recommended fix:** Reject any request whose `context.Items["TenantId"]` (header/subdomain) does not match the authenticated principal's `tid` claim, unless the principal carries `key_type=management` OR is a Platform/Partner-tenant role (cross-tenant access is legitimate only for those callers). Implement as a small middleware between `UseAuthorization` and the endpoint pipeline:

```csharp
// New: TenantBoundaryValidationMiddleware (insert at Program.cs:1219+)
public async Task InvokeAsync(HttpContext context)
{
    var principal = context.User;
    if (principal?.Identity?.IsAuthenticated != true) { await _next(context); return; }
    if (principal.FindFirst("key_type")?.Value == "management") { await _next(context); return; }

    var jwtTid = principal.FindFirst("tid")?.Value
              ?? principal.FindFirst("tenant_id")?.Value;
    if (!context.Items.TryGetValue("TenantId", out var v) || v is not TenantId resolved
        || jwtTid is null
        || string.Equals(jwtTid, resolved.Value, StringComparison.OrdinalIgnoreCase))
    {
        await _next(context); return;
    }

    // Permit if caller is host or partner (their tenant claim resolves to TenantType.Platform/Partner)
    var store = context.RequestServices.GetRequiredService<ITenantStore>();
    var callerTenant = await store.GetAsync(jwtTid);
    if (callerTenant?.Type is TenantType.Platform or TenantType.Partner) { await _next(context); return; }

    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    await context.Response.WriteAsJsonAsync(new ErrorResponse("Tenant header does not match authenticated principal."));
}
```

Add a regression test `TenantBoundary_ShouldReturn403_WhenAdminCallsForeignTenantViaHeader` covering `/admin/users`, `/admin/queues`, `/admin/agents`, `/admin/audit/*` for a Customer-tenant Admin against a different Customer tenant. The threat-model row 6.2 should also flip from ✅ to 🟡 until the middleware lands.

**Status:** OPEN — **blocks public flip.**

---

### PREPUB-2026-05-09-ADMIN-001 — OIDC client secret stored and returned in plaintext (P0, Scope 5.1 + 5.4)

**Severity:** P0 (plaintext secret persistence and unredacted reveal — checklist item 5.1 explicitly grades plaintext persistence as P0)
**Scope:** Scope 5.1 (DataProtection wrap on stored secrets) + Scope 5.4 (reveal-once / fingerprint-only) + Scope 1.3 (cryptographic failures)
**Affected:**
- `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs:30-77` (INSERT/UPDATE writes `oidc_client_secret` raw)
- `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs:96,120` (SELECT returns it raw)
- `src/Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs:33-37` (`GET /admin/auth/config` returns the entire `TenantAuthConfig` including `OidcClientSecret`)
- `src/Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs:66` (`PUT /admin/auth/config` accepts plaintext)
- `src/Verbara.Platform.Identity/TenantAuthConfig.cs:19` (no `[DataProtect]` / wrapper)
- `src/Verbara.Platform.Api/Endpoints/OidcEndpoints.cs:140` (consumed plaintext in token exchange)

**Observation:** The OIDC client-secret round-trips plaintext through database, API, and DTO. There is no `IDataProtectionProvider.CreateProtector(...).Protect(...)` call anywhere in the read or write path (verified by `grep -rn "Protect(" src/Verbara.Platform.Storage.Postgres/Stores/`). `GET /admin/auth/config` returns the entire stored row — including the secret — to any caller satisfying the `AdminOnly` policy. Combined with finding `MT-001`, a tenant Admin who pivots via `X-Tenant-Id` reads any other tenant's IdP client secret directly.

Audit-checklist item 5.1: "Every secret column has matching Protect/Unprotect pair. **Plaintext persistence = P0.**" Item 5.4: "Initial create returns full key; subsequent GET returns hash + prefix only." Both currently fail.

**Risk:** A DB backup, a SQL-injection elsewhere, a snapshot of the `tenant_auth_config` table, or a foreign-tenant Admin (via `MT-001`) yields the plaintext OAuth client secret for the operator's IdP integration. The secret typically grants the `client_credentials` flow against the IdP — full administrative access to whatever scope was provisioned.

**Recommended fix:**
1. Wrap on write / unwrap on read in `PostgresTenantAuthConfigStore`:
   ```csharp
   private readonly IDataProtector _protector;
   // ctor: _protector = dataProtectionProvider.CreateProtector("Verbara.OidcClientSecret");
   // SaveAsync: anonymous obj uses ... OidcClientSecret = config.OidcClientSecret is null ? null : _protector.Protect(config.OidcClientSecret), ...
   // GetAsync row mapping: OidcClientSecret = oidc_client_secret is null ? null : _protector.Unprotect(oidc_client_secret), ...
   ```
2. Redact on read in `AuthAdminEndpoints.GetConfig`: project to a DTO that emits `OidcClientSecretSet: bool` instead of the value, and a `OidcClientSecretFingerprint: string` (first 8 hex of SHA-256). Match the API-key reveal-once pattern documented at Scope 5.4.
3. Add a one-shot migration that wraps existing rows. Document in ADR (extend ADR-0003) that all column-level secret persistence MUST go through `IDataProtectionProvider`.
4. Add regression test `GetConfig_ShouldReturnFingerprintOnly_WhenOidcClientSecretSet` and `Save_ShouldPersistEncrypted_WhenOidcClientSecretProvided` (assert raw row value differs from input).

**Status:** OPEN — **blocks public flip.**

---

### PREPUB-2026-05-09-MFA-001 — `?targetTenant=` override on `/management/mfa/users/*` accepts any tenant id without ownership check (P1, Scope 2.1 + 2.5 + 3.4)

**Severity:** P1 (tenant-boundary bypass — a Partner Admin can reset MFA on, and revoke sessions of, users in arbitrary unrelated tenants)
**Scope:** Scope 2.1 (read filter) + 2.5 (admin operations scoped) + Scope 3.4 (MFA recovery requires elevated role; recovery without scope = privilege escalation)
**Affected:**
- `src/Verbara.Platform.Api/Endpoints/Mfa/MfaAdminEndpoints.cs:55,59,85,89` (handlers accept `?targetTenant=`)
- `src/Verbara.Platform.Api/Endpoints/Mfa/MfaAdminEndpoints.cs:128-131` (`ResolveTargetTenant` is `string.IsNullOrWhiteSpace(overrideTenant) ? actorTenant : new TenantId(overrideTenant)` — no validation)

**Observation:** `MfaAdminGate` is `PlatformAdminRequirement("security.mfa.admin")` — passes if the caller's tenant is the host *or* a Partner. A Partner Admin therefore reaches `ResetMfa` and `RevokeSessions`. The handler accepts `?targetTenant=victim` and does nothing to validate that `victim` is a child of the calling Partner. Compare with `ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync` (line 286) which walks the parent chain to enforce the same constraint for impersonation.

```csharp
// MfaAdminEndpoints.cs:128-131
private static TenantId ResolveTargetTenant(TenantId actorTenant, string? overrideTenant) =>
    string.IsNullOrWhiteSpace(overrideTenant)
        ? actorTenant
        : new TenantId(overrideTenant);
```

**Repro:** Partner-A Admin (tenant `partner-a` with `security.mfa.admin`) issues:
```http
POST /api/v1/management/mfa/users/<victim-user-id>/reset?targetTenant=partner-b-customer
Authorization: Bearer <partner-a-admin-jwt>
```
Service calls `IMfaAdminService.ResetMfaAsync(new TenantId("partner-b-customer"), ...)`. If the user id exists in `partner-b-customer`, MFA is wiped and the user becomes phishable on the next login. Audit emits but with `target_tenant_id="partner-b-customer"` from a Partner-A actor — the abnormality is captured but only after damage.

**Risk:** Account takeover prerequisite. The Partner Admin, plus a separately-acquired password (phishing, breach reuse), now has password-only access to a victim user in an unrelated tenant — MFA was the second factor. `RevokeSessions` is similarly weaponized to log victims out at will (denial of service or to force re-auth at a phishing proxy).

**Recommended fix:** Mirror the impersonation pattern. Inject `ITenantStore`, accept `?targetTenant`, and require either (a) the caller is the host tenant, or (b) `IsTenantInCallerHierarchyAsync(store, callerTenantId, requestedTenantId, ct)` returns true. Reject with `403` and emit `AuthEventTypes.MfaPrivilegeEscalationAttempted` (new) audit category otherwise.

```csharp
// Replace ResolveTargetTenant with an async authorized resolver
private static async Task<(TenantId? Tenant, IResult? Error)> ResolveTargetTenantAsync(
    HttpContext context, ITenantStore store, TenantId actorTenant, string? overrideTenant, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(overrideTenant)) return (actorTenant, null);
    if (string.Equals(actorTenant.Value, overrideTenant, StringComparison.Ordinal))
        return (actorTenant, null);
    var actor = await store.GetAsync(actorTenant.Value, ct);
    if (actor?.Type == TenantType.Platform) return (new TenantId(overrideTenant), null);
    if (await ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync(
            store, actorTenant.Value, overrideTenant, ct))
        return (new TenantId(overrideTenant), null);
    return (null, Results.Forbid());
}
```

Add regression test `ResetMfa_ShouldReturn403_WhenPartnerCrossesIntoForeignHierarchy`.

**Status:** OPEN — **blocks public flip.**

---

### PREPUB-2026-05-09-BILL-001 — Billing mutations (`Create/Update/Delete RateCard`, `GenerateInvoice`, `IssueInvoice`, `PayInvoice`, `UpdateQuota`, `PauseDunning`) emit no audit entries (P1, Scope 1.10 + 4.4)

**Severity:** P1 (billing — money-mutating operations — without an audit trail; SOC 2 + financial-controls blocker; threat-model 6.3 "Operator denies performing an admin action" was rated ✅ Verified by sample, this audit confirms the sample missed billing entirely)
**Scope:** Scope 1.10 (sensitive endpoints emit `IAuditService.AppendAsync`) + Scope 4.4 (audit emission failure does not silently drop)
**Affected:** `src/Verbara.Platform.Api/Endpoints/ManagementBillingEndpoints.cs`
- `CreateRateCard` (line 57-77), `UpdateRateCard` (79-105), `DeleteRateCard` (107-115) — no audit
- `GenerateInvoice` (130-149), `IssueInvoice` (161-173), `PayInvoice` (368-406) — no audit
- `UpdateQuota` (235-263), `PauseDunning` (287-308) — no audit
- Only `ResumeDunning` (317-366) emits `billing.dunning.resumed`

**Observation:** `IAuditService` is not even injected into the handlers above — confirmed by `grep -n "IAuditService" ManagementBillingEndpoints.cs` returning two hits, both in `ResumeDunning`. Mutations span the full money lifecycle (rate-card change → invoice generation → invoice issuance → payment → quota change → dunning pause/resume) and only `ResumeDunning` is reconstructable from the audit log.

**Risk:** A platform admin who issues a fraudulent invoice (or pays one without funds clearing), changes a customer rate card to a discounted curve, suspends dunning to hide a delinquency, or pays themselves an invoice via `PayInvoice` (which also flips `TenantStatus.Active` and clears `tierCache`/`featureGateCache`) leaves no record. This is the canonical "billing fraud" SOC 2 scenario and one of the categories explicitly enumerated in `audit-checklist.md` Scope 1.10 ("All sensitive mutations emit `IAuditService.AppendAsync`"). Public source makes the omission visible to attackers and to compliance reviewers reading the GitHub repo.

**Recommended fix:** Add `[FromServices] IAuditService audit` + `HttpContext context` to every handler above and emit the same shape `ResumeDunning` already uses. Categories: `category="billing"`, severity `"warning"` for create/update/issue/pay, `"critical"` for delete. Each entry must include actor, target (rate-card id / invoice id / tenant id), and the changed fields via `AuditChanges(Before, After)`. Cover `PayInvoice` specifically with `payment_status_before/after` and `tenant_status_before/after`. Add tests `Create*_ShouldEmitAudit_WhenSucceeds` for the 8 handlers.

**Status:** OPEN — **blocks public flip.**

---

### PREPUB-2026-05-09-BILL-002 — `PayInvoice` path-tenant trust (no caller cross-check, no input validation on `invoiceId`) (P1, Scope 1.2 + 2.1)

**Severity:** P1 (allows any `PlatformAdmin` to clear dunning + reactivate any tenant by passing a known invoice id; combined with PREPUB-MT-001 also reachable via cross-tenant header escalation)
**Scope:** Scope 1.2 (broken access control on tenant-scoped admin) + Scope 2.1 (read filter)
**Affected:** `src/Verbara.Platform.Api/Endpoints/ManagementBillingEndpoints.cs:368-406`

**Observation:** `PayInvoice` accepts only `{invoiceId}` from the URL. The handler:

```csharp
// ManagementBillingEndpoints.cs:368-406 (excerpt)
var dunningRecord = await dunningStore.GetByInvoiceAsync(invoiceId, ct);
if (dunningRecord is null) return Results.NotFound();
var invoice = await invoiceStore.GetByIdAsync(
    new TenantId(dunningRecord.TenantId), EntityId.From(invoiceId), ct);
...
await tenantStore.UpdateStatusAsync(dunningRecord.TenantId, TenantStatus.Active, ct);
tierCache.Remove(dunningRecord.TenantId); featureGateCache.Remove(dunningRecord.TenantId);
```

The tenant is derived from the dunning record itself. Anyone who satisfies `PlatformAdminOnly` and supplies a valid `invoiceId` can mark it paid AND flip the owning tenant from `Suspended` to `Active`. There is no input-shape validation (`invoiceId` is a free-form string fed to `EntityId.From` without length/format check) — combined with finding `BILL-001`, no audit trail records who paid what.

**Risk:** A compromised PlatformAdmin credential or a partner admin who escalates via `MT-001` can reactivate any suspended tenant by guessing or enumerating invoice ids (typically Guid-N format from `EntityId.New()`, low entropy across a leaked-pair). Combined with `BILL-001` the action leaves no trace.

**Recommended fix:** Either (a) require the caller to pass `?tenantId=` and assert it matches `dunningRecord.TenantId` (defense-in-depth — surfaces accidental cross-tenant pays), or (b) add the `IAuditService` emission per `BILL-001` so the action is at minimum recorded. Both should land. Validate `invoiceId` shape against `EntityId.IsValid` before the store call to fail fast on garbage inputs. Test `PayInvoice_ShouldRecordAudit_WhenSucceeds` and `PayInvoice_ShouldReject_WhenTenantIdMismatchesDunningRecord`.

**Status:** OPEN — **blocks public flip.**

---

### PREPUB-2026-05-09-ADMIN-002 — Management API key bypasses every `PlatformAdminRequirement` permission check, including `security.jwt.rotate`, `audit.export`, `retention.manage` (P1, Scope 1.1 + 5.5)

**Severity:** P1 (single bearer credential that strictly exceeds the permission model; rotation hygiene loss; mismatch with the documented "double-lock" pattern)
**Scope:** Scope 1.1 (broken access control — `/management/*` correct policy) + Scope 5.5 (rotation invalidates across consumers)
**Affected:** `src/Verbara.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs:27-32`

**Observation:** Every R5.2/R5.4 admin policy is wired as `PlatformAdminRequirement("xxx")` — explicitly documented as "double-locked" with a permission seed (e.g. `Program.cs:973-976` for `security.mfa.admin`, `:1015-1017` for `security.jwt.rotate`). Yet the handler short-circuits BEFORE any permission check when `key_type=management`:

```csharp
// PlatformAdminAuthorizationHandler.cs:27-32
var keyTypeClaim = context.User.FindFirst("key_type")?.Value;
if (keyTypeClaim == "management")
{
    context.Succeed(requirement);
    return;
}
```

A management API key (issued via `/api/setup` or `/management/api-keys`) — a single long-lived bearer — therefore satisfies `MfaAdminGate`, `AuditAdminGate`, `AuditAdminExportGate`, `ImpersonationAdminGate`, `RetentionReadGate`, `RetentionManageGate`, `JwtKeyRotationGate`, and the bare `PlatformAdminOnly`. The permission seeds are ornamental for that auth path.

**Risk:** Management API key compromise = unconditional control over MFA reset, JWT signing-key rotation, audit log export, retention dry-run toggle, and tenant lifecycle. The threat-model assumes "rotation handlers emit audit + flush relevant cache" (Scope 5.5 ✅) — but a leaked management key does not need to rotate, it persists across user MFA enrollment and password rotation. There is no per-key permission scope (the key carries `scopes=["platform:*"]` per `ManagementApiKeyEndpoints.cs:68`).

**Recommended fix:** Either (a) enforce `requirement.Permission` against the API key's `scopes` array, or (b) require management API keys to declare an explicit permission whitelist at creation time and reject the bypass when the requested permission is absent. Minimum: add a startup-warn log when a management API key is used to reach a permission-gated surface AND the permission is not in the API key's recorded scopes. Track in v2.0.x as `ADMIN-002: scope-aware management API keys`. Recommend a concurrent `key_age_days` constraint test for the JWT key rotation surface (refuse `mgmt_*` keys older than 90 days for `security.jwt.rotate`).

**Status:** OPEN — **blocks public flip** unless explicitly accepted as documented residual exposure (in which case demote to P2 with sign-off and document in `threat-model.md` §8 "Open risks").

---

### PREPUB-2026-05-09-ADMIN-003 — `Setup` endpoint pins host-tenant id to constant `"platform"` and silently swallows RBAC failures (P2, Scope 1.1 + 1.10)

**Severity:** P2 (defense-in-depth; gap is operationally invisible until cross-cluster recovery)
**Scope:** Scope 1.1 (admin endpoints policy) + Scope 1.10 (sensitive mutations audited)
**Affected:** `src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs:40,99-116`

**Observation:** `Setup` (anonymous, idempotency-guarded by "host tenant absence") hard-codes `var hostTenantId = "platform"` and swallows the entire RBAC clone+assign block in `try { ... } catch { }` (line 112-116). No audit emission occurs. If the RBAC store is partially functional, the operator gets a "successful" setup with a user holding only the `UserRole.Admin` fallback — no `platform:*` permissions — and no record of why. The `audit_entries` row that should mark first-deploy is also absent.

**Risk:** Recovery scenarios (reinstall after data-loss, multi-region failover) silently issue a partially-privileged admin token. The constant tenant id `"platform"` is documented in CLAUDE.md so it is not a secret, but the public-source flip makes any `?tenantId=platform`-shaped probe trivially scriptable; the absence of audit makes the first-deploy step invisible to compliance.

**Recommended fix:**
- Replace `try { ... } catch { }` with explicit logging of the exception and a setup result that surfaces "rbac_partial=true" so operators can re-run the seeding step.
- Emit `IAuditService.AppendAsync` with `category="admin"`, `action="platform.initialized"`, `severity="critical"` on success.
- Make the host tenant id configurable via `IOptions<PlatformOptions>` (default `"platform"`) so multi-cluster deployments can avoid id collisions.
- Add `Setup_ShouldEmitAudit_WhenSucceeds` and `Setup_ShouldReturnPartial_WhenRbacFails`.

**Status:** OPEN — track as v2.0.x ticket (does not block flip; audit gap is hygiene, not exploitable in steady state).

---

### PREPUB-2026-05-09-MFA-002 — `MfaSetup` (legacy) and `MfaEnrollEndpoints.Init` allow re-enrollment without password / current-MFA step-up (P2, Scope 3.4)

**Severity:** P2 (defense-in-depth; mitigated in practice by JWT-bound session, but absent step-up means an XSS or stolen-cookie attacker can replace MFA without re-auth)
**Scope:** Scope 3.4 (MFA enrollment + verification audited; bypass on lost-device requires admin recovery)
**Affected:**
- `src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs:629-651` (legacy `MfaSetup` writes a fresh `MfaSecret` over the existing one with no password check)
- `src/Verbara.Platform.Api/Endpoints/Profile/MfaEnrollEndpoints.cs:57-79` (`Init` short-circuits if `user.MfaEnabled` is true; `Verify` requires no password before persist)

**Observation:** Compare with `MfaDisable` (line 696-746) which correctly demands the user's password (`PasswordService.VerifyPassword(body.Password, user.PasswordHash)`) and enforces the tenant policy `IsMfaRequiredForRole`. Re-enrollment paths are weaker than disable. `MfaSetup` (legacy) writes the new secret to the user record IMMEDIATELY (line 646-648) before the user has even seen the QR code — if the request is intercepted or the response is dropped, the user is stranded on a secret they never saw. The new wizard pattern (`MfaEnrollEndpoints`) defers persistence to `Verify` correctly, but neither path requires the user to enter their current password OR a current TOTP code before binding a new secret.

**Risk:** A stolen session token or an XSS-injected fetch to `/api/v1/auth/mfa/setup` (legacy) replaces the user's MFA secret before the user notices, then the attacker submits `MfaConfirm` with a fresh TOTP — second-factor takeover without any out-of-band signal to the user beyond the existing notification (which fires only on `MfaConfirm` success, line 681). On the wizard path the persist happens at `Verify`; same exposure window.

**Recommended fix:**
- Require `?password=` or `?currentTotp=` body field on `MfaSetup` and `MfaEnrollEndpoints.Verify` when `user.MfaEnabled` is true. Reject with `400` otherwise.
- Emit `AuthEventTypes.MfaSecretRebindAttempted` audit event on every `Verify`/`Setup` regardless of success.
- Send the existing `INotificationService.CreateAsync(..., "security.mfa_rebound", ...)` notification on `Verify` success, not only `Confirm`.

**Status:** OPEN — track as v2.0.x ticket (extension of the MFA hardening epic that already produced `MFA-007`).

---

### PREPUB-2026-05-09-MT-002 — `/management/webhooks/dead-letter` accepts unauthenticated `?tenantId=` for cross-tenant scoping (P2, Scope 2.1 + 5.5)

**Severity:** P2 (defense-in-depth; PlatformAdminOnly + management API-key abuse path)
**Scope:** Scope 2.1 (tenant filter) + Scope 5.5 (rotation cache invalidation — webhook keys)
**Affected:** `src/Verbara.Platform.Api/Endpoints/ManagementWebhookEndpoints.cs:17-32`

**Observation:** `ListDeadLetter` requires `?tenantId=` as a query parameter and trusts it without cross-checking against the JWT principal. `RetryDeadLetter` looks up the delivery by id and operates on it without verifying that the resolved delivery belongs to the requested `tenantId`. The handler also does not emit audit on retry (cross-cuts `BILL-001`'s audit-omission pattern).

**Risk:** Mostly contained by `PlatformAdminOnly` (host or partner caller). Combined with `ADMIN-002`, a leaked management API key reads any tenant's webhook delivery payloads (which can carry conversation snapshots, contact PII, OAuth bearer responses for outbound integrations).

**Recommended fix:** Validate `delivery.TenantId == queryTenantId` in `RetryDeadLetter`. Add `IAuditService` emission `webhook.dead_letter.retried`. Add `?tenantId` cross-validation against caller hierarchy when caller is `Partner` (mirror `MFA-001` fix). Track as v2.0.x.

**Status:** OPEN — track as v2.0.x.

---

## Cross-references to existing v1.13.x tickets

- **AUTH-002 (audit 2026-04)** — `?token=` / `?access_token=` query-string token acceptance. **CLOSED in v1.14.4** per `Auth/AuthSchemeConfiguration.cs:46-62` (`IsQueryTokenPathAllowed` whitelist) and `:73-84` (`OnMessageReceived` mirror guard). Threat-model row 6.4 ("TA1 captures a JWT from server access logs because it was passed in `?token=`") should be flipped from 🟡 Tracked to ✅ Verified in the next status-update entry of `threat-model.md`.
- **CFG-003 (audit 2026-04)** — Plaintext placeholder credentials in `appsettings.Development.json`. **STILL PENDING.** Confirmed unchanged; the Development file still ships and is included in the publish output. Continues to apply to public-flip readiness; track for v2.0.x (continuing the scope inherited from the original v1.13.x filing). Not re-flagged here.
- **MFA-007 (audit 2026-04)** — In-memory default for `IJtiRevocationCache`/`IMfaPendingCache`. **STILL PENDING.** Confirmed unchanged; Redis variant ships in `Verbara.Platform.Identity.Redis`, default wiring still in-memory, no fail-loud guard in production. Continues to apply.
- **MT-005 (audit 2026-04)** — `analytics_interval_snapshots` 3-table CHECK constraint coverage. Out of scope for Trigger 3 (Pro-side schema). Untouched.
- **AUDIT-006 (audit 2026-04)** — DB-level UPDATE/DELETE prevention on `audit_entries`. Out of scope for Trigger 3 (operator DB-grant hygiene). Untouched.
- **DOC-004 (audit 2026-04)** — Security-headers verification (HSTS/CSP/etc.). `grep -rn "UseHsts\|UseSecurityHeaders\|Content-Security-Policy" src/` still returns no app-side hits — the operational expectation continues to be reverse-proxy-applied. Not blocking.
- **IMP-008 (audit 2026-04)** — Impersonation audit captures actor+target+reason+IP. **VERIFIED RE-PRESENT** in `ManagementImpersonationEndpoints.cs:429-448` (revoke) and the dual-audit pattern at `:222-256` (start). Continues ✅.

---

## Remediation status

- [ ] All P0 findings fixed before flip — **MT-001** (cross-tenant header escalation), **ADMIN-001** (OIDC plaintext)
- [ ] All P1 findings fixed before flip — **MFA-001** (`?targetTenant=` bypass), **BILL-001** (billing audit gap), **BILL-002** (`PayInvoice` trust), **ADMIN-002** (management-key permission bypass)
- [ ] P2 findings tracked as v2.0.x tickets — **ADMIN-003** (Setup hard-codes/swallows), **MFA-002** (no step-up on re-enroll), **MT-002** (webhook DLQ tenant trust)
- [ ] Threat-model `Status updates` entry appended to flip AUTH-002 row to ✅ and add ⚠️ rows for MT-001 + ADMIN-001 + MFA-001 + ADMIN-002

## Sign-off

**Trigger 3 status: PENDING — BLOCKED.**

Blocker IDs: `PREPUB-2026-05-09-MT-001`, `PREPUB-2026-05-09-ADMIN-001`, `PREPUB-2026-05-09-MFA-001`, `PREPUB-2026-05-09-BILL-001`, `PREPUB-2026-05-09-BILL-002`, `PREPUB-2026-05-09-ADMIN-002`.

The 4 P1 findings and 2 P0 findings each have a documented exploit path that becomes grep-able once the repository goes public. None require sophisticated tooling — `MT-001` is a single header swap; `ADMIN-001` is a single GET; `MFA-001` is a single query parameter; `BILL-001` is the absence of records (visible to any compliance reviewer); `ADMIN-002` is a 6-line bypass in the auth handler that any reader of `Auth/PlatformAdminAuthorizationHandler.cs:27-32` can identify in seconds.

This auditor recommends the trigger remain **OPEN (RED)** until at minimum the two P0 findings are remediated and merged. The four P1 findings should land in the same v2.0.x patch train; demoting any to P2 requires explicit ADR sign-off (extend ADR-0018) documenting the residual exposure in `threat-model.md` §8.

**Auditor note:** The 2026-04 audit's Scope 2.1 verification ("10/10 sampled endpoints filter") was sound for the management surface but did not sample the bare `AdminOnly` surfaces (`/admin/users`, `/admin/queues`, `/admin/agents`, `/admin/audit`). The flat `RequireRole("Admin")` policy combined with header-driven tenant resolution is the proximate cause of `MT-001`. Recommend that future audits explicitly sample BOTH the `PlatformAdminOnly` and the bare `AdminOnly` groups, and that `audit-checklist.md` Scope 2.1 be amended to call out "test against `AdminOnly` policies specifically".
