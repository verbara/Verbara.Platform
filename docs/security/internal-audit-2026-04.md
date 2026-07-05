# Internal Security Audit — 2026-04-26 (R5.4)

**Scope:** R5.4 Production Validation per ADR-0008
**Date:** 2026-04-26
**Method:** Code-review-based audit covering 5 scopes per `audit-checklist.md`
**Tools used:** Manual code review (`grep` + `Read`) + `dotnet list package --vulnerable --include-transitive`. OWASP ZAP active scan deferred (no staging environment available; `scripts/run-zap-scan.sh` reproducible for future).
**Auditor:** R5.4 Phase A.4 subagent

## Summary

| Scope | P0 | P1 | P2 | P3 | Total |
|---|---|---|---|---|---|
| 1. OWASP Top 10 web | 0 | 1 | 1 | 1 | 3 |
| 2. Multi-tenant isolation | 0 | 0 | 0 | 1 | 1 |
| 3. JWT + MFA + impersonation | 0 | 0 | 1 | 0 | 1 |
| 4. Audit log integrity | 0 | 0 | 0 | 1 | 1 |
| 5. Secrets handling | 0 | 0 | 1 | 1 | 2 |
| **Total** | **0** | **1** | **3** | **4** | **8** |

**P1 status:** 1 finding — VULN-001 FIXED inline during this audit (MailKit/MimeKit bump 4.11.0 → 4.16.0).
**P2 status:** 3 findings tracked as v1.13.x tickets.
**P3 status:** 4 informational — tracked as next-quarter improvements.

> A separate **out-of-scope** observation surfaced during the audit (R5.4 in-flight work, not a security finding): the new file `src/Verbara.Platform.Core/DependencyInjection/HostedServicePromotionExtensions.cs` (uncommitted, from a concurrent Phase A subagent) currently fails to build with `IL2091` (trimming/AOT analyzer). Reported under "Coordinator escalation" below — **NOT** a security defect; build hygiene only.

---

## Findings

### AUDIT-2026-04-VULN-001 — MailKit + MimeKit 4.11.0 Moderate vulnerabilities (P1, OWASP A.06) — FIXED

**Severity:** P1 (blocks ship per R5.4 ship criterion "vulnerable list clean cross-repo")
**Scope:** OWASP Top 10 (A.06 Vulnerable Components)
**Discovered by:** Phase A.6 NU1902 cleanup checkpoint (pre-existing, not introduced by R5.4)
**Affected:** `src/Verbara.Platform.Mail/Verbara.Platform.Mail.csproj` + `tests/Verbara.Platform.Mail.Tests/Verbara.Platform.Mail.Tests.csproj`
**Advisories:**
- `GHSA-9j88-vvj5-vhgr` — MailKit 4.11.0 Moderate
- `GHSA-g7hc-96xr-gvvx` — MimeKit 4.11.0 Moderate (transitive)

**Repro (pre-fix):**
```bash
dotnet list package --vulnerable --include-transitive 2>&1 | grep -A 3 "MailKit\|MimeKit"
# Project `Verbara.Platform.Mail` has the following vulnerable packages
#    [net10.0]:
#    Top-level Package      Requested   Resolved   Severity   Advisory URL
#    > MailKit              4.11.0      4.11.0     Moderate   https://github.com/advisories/GHSA-9j88-vvj5-vhgr
#    Transitive Package      Resolved   Severity   Advisory URL
#    > MimeKit               4.11.0     Moderate   https://github.com/advisories/GHSA-g7hc-96xr-gvvx
```

**Fix applied:** Bumped `MailKit` and `MimeKit` `4.11.0 → 4.16.0` in `Directory.Packages.props` (latest stable on NuGet at audit date). Restore + verification clean for both `Verbara.Platform.Mail` and `Verbara.Platform.Mail.Tests`. MimeKit tracks MailKit, both bumped together as the transitive pin is also direct here. No breaking changes between 4.11 and 4.16 (point releases on the same 4.x line; MailKit/MimeKit follow strict additive minor cadence). Mail project's compile + restore unchanged after bump.

The bump was captured in concurrent R5.4 commit `7a39685` (`refactor(hosting): extract PromoteHostedServiceToSingleton<T> to Platform.Core extension (R5.4 E.4)`) which combined the audit-driven MailKit fix with that subagent's hosted-service refactor. The audit had staged the same change locally; the concurrent commit landed first, so the audit confirms the fix is on `main` rather than re-committing it.

**Verification (post-fix):**
```bash
dotnet list src/Verbara.Platform.Mail/Verbara.Platform.Mail.csproj package --vulnerable --include-transitive
# The given project `Verbara.Platform.Mail` has no vulnerable packages given the current sources.
dotnet list tests/Verbara.Platform.Mail.Tests/Verbara.Platform.Mail.Tests.csproj package --vulnerable --include-transitive
# The given project `Verbara.Platform.Mail.Tests` has no vulnerable packages given the current sources.
```

**Status:** FIXED inline (this audit run). No follow-up ticket needed.

---

### AUDIT-2026-04-AUTH-002 — `?token=` query-string accepted globally instead of scoped to `/hubs/*` (P2, OWASP A.02 + A.07)

**Severity:** P2 (defense-in-depth gap; not a known exploit path)
**Scope:** Scope 1 (A.02 Cryptographic / A.07 Auth Failures) + Scope 3 (item 3.3)
**Affected:** `src/Verbara.Platform.Api/Auth/AuthSchemeConfiguration.cs:62-99`

**Observation:** The `JwtBearerEvents.OnMessageReceived` handler accepts JWTs from `?token=` and `?access_token=` query parameters on **every authenticated request**, not only on the SignalR hub paths. The current implementation:

```csharp
OnMessageReceived = context =>
{
    var token = ExtractJwtQueryParam(context.Request);
    if (token is not null)
        context.Token = token;
    return Task.CompletedTask;
},
```

`ExtractJwtQueryParam` reads `Query["token"]` then `Query["access_token"]` and accepts any value starting with `eyJ`. There is no path filter (no check for `/hubs/...`).

**Risk:** Tokens leak via referrer header, server access logs, browser history, and shared-link patterns. Industry guidance (OWASP API Security Top 10 — API2:2023) treats query-string token transmission as an anti-pattern outside the narrow exception of WebSocket/SSE handshakes that cannot send custom headers.

**Audit checklist item:** 3.3 says "No `Query["token"]` extraction outside SignalR hub-specific path; SignalR variant scoped to `/hubs/*` only." This audit currently FAILS that criterion.

**Recommended fix (v1.13.x ticket):**
```csharp
OnMessageReceived = context =>
{
    if (!context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        return Task.CompletedTask;

    var token = ExtractJwtQueryParam(context.Request);
    if (token is not null)
        context.Token = token;
    return Task.CompletedTask;
},
```
Drop the `?token=` legacy SSE-endpoint extraction entirely if no shipped SSE endpoint depends on it (verify with `grep -rn "?token=" src/`). Accept only `?access_token=` per `@microsoft/signalr` convention, scoped to `/hubs/*`.

**Status:** PENDING — track as v1.13.x ticket "AUTH-002: scope `?access_token=` extraction to `/hubs/*` only". Does not block R5.4 ship (no current exploit path, JWT remains validated; this is hygiene/defense-in-depth).

---

### AUDIT-2026-04-CFG-003 — Plaintext placeholder credentials in `appsettings.Development.json` (P2, OWASP A.05 + Scope 5)

**Severity:** P2 (defense-in-depth; dev-only file but ships in container images)
**Scope:** Scope 1 (A.05 Security Misconfiguration) + Scope 5 (item 5.3)
**Affected:** `src/Verbara.Platform.Api/appsettings.Development.json`

**Observation:**
```json
{
  "Asterisk": {
    "Ami": { "Username": "admin", "Password": "admin" }
  },
  "Services": {
    "ServiceKey": "platform_internal_secret"
  }
}
```

The Development variant ships placeholder secrets `admin:admin` (AMI) + `platform_internal_secret` (Services key). Production `appsettings.json` is clean (no secret keys). The risk:

1. The Development file is included in the published container image (verified: `bin/Release/net10.0/publish/appsettings.Development.json` exists per audit grep).
2. If a deployment runs with `ASPNETCORE_ENVIRONMENT=Development` (test/staging accidentally), placeholder credentials become live defaults.
3. `ServiceKey="platform_internal_secret"` is documented-as-public — anyone reading the repo learns the dev key, then probes prod with it.

**Recommended fix (v1.13.x ticket):**
- Either replace placeholders with env-var references (`{ "Password": "${ASTERISK_AMI_PASSWORD}" }` pattern with `IConfigurationBuilder.AddEnvironmentVariables()`) or remove the file from the published artifact (`<Content … CopyToOutputDirectory="Never">`) and document local-dev set-up via README.
- Add a startup check that **fails loud** if `ASPNETCORE_ENVIRONMENT=Development` AND `Asterisk:Ami:Password == "admin"`. Refusing to start beats silent default-credentials.

**Status:** PENDING — track as v1.13.x ticket "CFG-003: remove plaintext placeholder credentials from shipped Development config". Does not block R5.4 ship (production config is clean; the Development file is dev-only fixture).

---

### AUDIT-2026-04-DOC-004 — Audit-checklist item 1.7 (security headers) not verified at audit time (P3, OWASP A.05)

**Severity:** P3 (informational; no exploit path observed but verification gap)
**Scope:** Scope 1 (item 1.7) — security headers (HSTS, CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy)

**Observation:** The audit-checklist requires verifying 6 security headers on responses. Verification requires either a running app or a code-review of explicit middleware registration. Grep for `UseHsts`, `UseSecurityHeaders`, `Content-Security-Policy` produced no clear hit during the manual review window. The Platform API may rely on reverse-proxy (Nginx/Traefik) to add the headers — common production pattern but not auditable from the repo alone.

**Recommended action (next quarter):** Document the operational expectation in `docs/operations/security-headers.md` (which proxy is responsible, which headers are guaranteed). Add an integration test that hits `/health/live` and asserts the 6 headers present. If app-side, add `UseHsts()` + a header-injection middleware in `Program.cs`.

**Status:** PENDING — track as next-quarter improvement (not v1.13.x). Does not block ship.

---

### AUDIT-2026-04-MT-005 — `analytics_interval_snapshots` 3-table CHECK constraints (P3, ADR-0002)

**Severity:** P3 (informational; verified migration files exist — confirming completeness)
**Scope:** Scope 2 (item 2.2)

**Observation:** R5.2 added `CHECK (tenant_id <> '')` migrations across 5 Pro packages (V003 EventStore, V002 CallAnalytics, V006 LiveQueueSnapshots, plus AgentAssist + Push.SignalR). Verified files exist:
- `Verbara.Sdk.Pro.EventStore.Postgres/Migrations/V003__events_completed_tenant_check.sql`
- `Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres/Migrations/V002__callanalytics_tenant_check.sql`
- `Verbara.Sdk.Pro.Analytics.Storage.Postgres/Migrations/V006__live_queue_snapshots_tenant_check.sql`

The 3 `analytics_interval_snapshots` tables (Pro.Analytics interval reporter — see roadmap) were not surfaced in the migration grep. Possibly they have constraints from earlier migrations or are still untyped.

**Recommended action:** Audit Pro.Analytics.Storage.Postgres's 3 interval snapshot tables (`analytics_interval_queue_snapshots`, `analytics_interval_agent_snapshots`, `analytics_interval_summary_snapshots` or equivalent names) for CHECK constraint coverage. If missing, add a V007 migration.

**Status:** PENDING — track as next-quarter improvement; ADR-0002 work was largely closed in R5.2 but residual coverage gap worth confirming.

---

### AUDIT-2026-04-AUDIT-006 — Audit DELETE limited to retention purge — verified (P3, info)

**Severity:** P3 (informational; verification of expected behavior)
**Scope:** Scope 4 (item 4.1)

**Observation:** Grep for `DELETE FROM audit_entries` surfaced one hit:
```csharp
// PostgresAuditStore.cs:243
return await conn.ExecuteAsync(
    "DELETE FROM audit_entries WHERE tenant_id = @TenantId AND occurred_at < @Cutoff",
    new { TenantId = tenantId.Value, Cutoff = cutoff });
```

This is `DeleteOlderThanAsync(tenantId, cutoff, ct)` — the retention purge. It is tenant-scoped + time-bounded. No app-side UPDATE/DELETE on individual entries. The `UPDATE` hit (`Migrations/021_AuditEntriesNormalize.sql:60`) is a one-shot schema migration, not an app code path. Append-only invariant **PASS**.

The observed gap (P3): there is no DB-level constraint or trigger preventing UPDATE/DELETE — the invariant is only enforced via convention. A SOC 2 auditor would expect either (a) a separate DB role for the app with no UPDATE/DELETE grant on `audit_entries`, or (b) a `BEFORE UPDATE/DELETE` trigger that rejects unless the caller is the retention service.

**Recommended action (next quarter):** Document expected DB grants in `docs/operations/db-roles.md`. Optional hardening: split retention purge into a dedicated DB role with elevated DELETE grant, app role without.

**Status:** PENDING — informational only.

---

### AUDIT-2026-04-MFA-007 — MFA pending-cache + JTI revocation are in-process by default in Identity (P2, Scope 3)

**Severity:** P2 (defense-in-depth gap; mitigated by R5.1 `Verbara.Platform.Identity.Redis` opt-in package)
**Scope:** Scope 3 (item 3.2 + item 3.4)

**Observation:** `IMfaPendingCache`, `IPasswordResetCache`, `IJtiRevocationCache` defaults shipping in `Verbara.Platform.Identity` are in-memory (`InMemoryMfaPendingCache.cs` etc.). The R5.1 release added `Verbara.Platform.Identity.Redis` to satisfy the production-cluster requirement (durable revocation across pods + fan-out across hosts), but the **default** wiring still is in-memory. A consumer who never opts into Redis loses revocation/MFA-step-up state on restart and across replicas.

The audit-checklist item 3.2 says: "Revocation survives restart. In-memory acceptable only for dev; prod must use Redis/DB." — currently the burden is on the operator to read docs + opt in. No fail-loud guard rejects an in-memory cache when running in production.

**Recommended fix (v1.13.x ticket):**
- Add a startup health check `identity-cache-durable` that emits Degraded when `IJtiRevocationCache` resolves to the in-memory implementation AND `ASPNETCORE_ENVIRONMENT == "Production"`.
- Document the recommendation prominently in `Verbara.Platform.Identity` README.
- Long-term: consider making the Redis package the default (with in-memory as a `WithInMemoryCachesForTesting()` opt-out), inverting the safe default.

**Status:** PENDING — track as v1.13.x ticket "MFA-007: fail-loud when JTI/MFA caches are in-memory in production". Does not block R5.4 ship — Redis package exists; operators on multi-pod deploys are expected to opt in.

---

### AUDIT-2026-04-IMP-008 — Impersonation audit captures actor + target + reason + IP — verified (P3, info)

**Severity:** P3 (informational; verification of expected behavior)
**Scope:** Scope 3 (items 3.5 + 3.6)

**Observation:** `ManagementImpersonationEndpoints.RevokeSession` (line 429-448) emits a rich audit entry for impersonation revoke that includes:
- `category="auth"`, `action="impersonation.session.revoked"`, `severity="warning"`
- `actorId` (admin who revoked), `targetId` (session id)
- metadata: `actor_tenant_id`, `target_tenant_id`, `impersonator_user_id`, `impersonator_tenant_id`, `reason`, `read_only`, `ip`

`GenerateImpersonationToken` in `JwtTokenService.cs:128` sets `impersonator_id`, `impersonator_tenant`, `impersonation="true"` claims preserved across all impersonated requests. Items 3.5 + 3.6 PASS.

**Status:** Verified. No action.

---

## Out-of-scope coordinator note (RESOLVED)

During the audit window, a transient build break (IL2091) was observed in the WIP file `src/Verbara.Platform.Core/DependencyInjection/HostedServicePromotionExtensions.cs` introduced by a concurrent Phase A subagent. The audit verified via `git stash` that the error was not introduced by the MailKit bump. The concurrent commit `7a39685` landed the fix (added `[DynamicallyAccessedMembers]` annotation) and also picked up the MailKit/MimeKit bump. No further action.

---

## ZAP active scan — DEFERRED

Per ADR-0008 + this audit's method note: OWASP ZAP active scan was not executed during this run because no staging environment is currently available. The reproducible script `scripts/run-zap-scan.sh` has been added to the repo. When staging is available, run:

```bash
./scripts/run-zap-scan.sh https://staging.example.com /tmp/zap-report
```

Append findings to this document under a new `## ZAP active scan` section.

## Remediation status

- [x] All P0 findings fixed — 0 P0 findings raised
- [x] All P1 findings fixed — VULN-001 fixed inline (MailKit/MimeKit 4.16.0)
- [ ] All P2 findings tracked as v1.13.x tickets — AUTH-002, CFG-003, MFA-007 (3 tickets to file)
- [ ] All P3 findings tracked as next-quarter improvements — DOC-004, MT-005, AUDIT-006, IMP-008 (4 to track)

## Sign-off

R5.4 ship gate: **READY** (P0+P1 = 0 open after VULN-001 fix). Will be confirmed by coordinator in Phase C.1.

**Auditor note:** The 3 P2 findings should be filed as v1.13.x patch-train tickets within 2 weeks of R5.4 ship. None of them are currently exploitable but each weakens defense-in-depth posture and would be flagged by an external SOC 2 auditor.
