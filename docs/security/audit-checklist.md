# Internal Security Audit Checklist (permanent template)

**Origin:** ADR-0008 (R5.4 Production Validation)
**Lifecycle:** Reused on every R5.x patch + future R6+ pre-ship audits.
**Method:** Code-review-based audit + (when staging available) OWASP ZAP active scan via `scripts/run-zap-scan.sh`.

> Each finding is filed with severity P0/P1/P2/P3 and tracked in the per-run report
> (`docs/security/internal-audit-YYYY-MM.md`). P0/P1 block ship; P2/P3 → patch tickets.

---

## Severity definitions

| Severity | Definition | Examples | Action |
|---|---|---|---|
| **P0** | Active exploit path or trivially exploitable critical vulnerability. Attacker can read/modify cross-tenant data, bypass auth, escalate privilege without prerequisites. | Auth bypass; RCE; SQL injection on prod path; cross-tenant data leak; secrets in logs. | **Block ship.** Fix before merge. Coordinator escalation immediate. |
| **P1** | Critical vulnerability requiring some prerequisite, OR known-vulnerable dependency with documented advisory affecting prod path. | Vulnerable NuGet (CVE/GHSA) on a shipped package; missing authorization on admin endpoint; MFA bypass with stolen primary credentials; weak crypto on token signing. | **Block ship.** Must be fixed (or upstream-patched + verified) before R5.x ships. |
| **P2** | Defense-in-depth gap. Not directly exploitable today but weakens posture against future change or accidental misuse. | Missing rate limit on admin endpoint; verbose error in dev path; audit log gap on rare action; secret-in-config that should be DataProtection-wrapped. | **Track as v1.13.x ticket.** Does not block ship; must close within next patch train. |
| **P3** | Informational / hardening recommendation. No exploit path, no known harm, but worth doing for hygiene. | Docs gap; CSP could be tighter; recommended SAST rule not enabled; audit log retention could be longer. | **Track as next-quarter improvement.** Does not block; nice-to-have. |

---

## Scope 1 — OWASP Top 10 (2021) web

Covers shipped HTTP surface: `/api/*`, `/management/*`, `/health/*`, `/swagger`, SignalR `/hubs/*`.

| ID | Item | Method | Pass criterion |
|---|---|---|---|
| 1.1 | **A.01 Broken Access Control** — `/management/*` endpoints | Code review: every `/management/*` endpoint has `RequireAuthorization` + correct permission policy (`PlatformAdmin` or specific permission). | All endpoints gated. No `MapGet` without `RequireAuthorization` in `/management/*` group. |
| 1.2 | **A.01 Broken Access Control** — `/api/v1/admin/*` and tenant-scoped admin | Code review: tenant-admin endpoints validate `ITenantContext` matches resource tenant. No cross-tenant resource access via path traversal. | Path-injection probe negative; tenant-id in URL ignored when conflicts with claim. |
| 1.3 | **A.02 Cryptographic Failures** — secret storage | Verify `IDataProtectionProvider` wrap on stored secrets (webhook keys, OIDC client secrets, API keys, OAuth refresh tokens). | All secret columns marked encrypted; no plaintext-secret read path in code. |
| 1.4 | **A.02 Cryptographic Failures** — token signing | JWT uses RS256/ES256 (not HS256 with shared secret); key from `IDataProtectionProvider` or external KMS; rotation supported. | `JwtTokenService` review confirms asymmetric algorithm or wrapped HMAC. |
| 1.5 | **A.03 Injection** — SQL | Grep for `FromSqlRaw`, `ExecuteRawSql`, raw `IDbConnection.Execute(string)` with interpolation. | No interpolated SQL on user input. All queries parameterized via Dapper `@param` or EF `FromSql`. |
| 1.6 | **A.03 Injection** — Command/LDAP/NoSQL | Grep for `Process.Start`, `Shell.Execute`, `LdapConnection`, `MongoClient` direct interpolation. | No shell interpolation; LDAP filters parameterized; NoSQL queries typed. |
| 1.7 | **A.05 Security Misconfiguration** — security headers | Verify HSTS, CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy emitted. | Header probe via curl shows all 6 headers on `/` and `/api/*` responses. |
| 1.8 | **A.06 Vulnerable Components** — NuGet vulnerabilities | `dotnet list package --vulnerable --include-transitive` clean across all .csproj. | Zero vulnerable packages in shipped projects. Test-only vulns documented as P3. |
| 1.9 | **A.07 Identification & Auth Failures** — brute force | Login endpoint has rate limit + lockout + audit emission. | `/api/auth/login` rate-limited per IP + per username; lockout after N failures; audit emits `auth.login.failed`. |
| 1.10 | **A.09 Logging+Monitoring** — sensitive endpoints | Sample audit emission for `/management/*`, impersonation, MFA enroll/disable, secret-rotate. | All sensitive mutations emit `IAuditService.AppendAsync` with actor + tenant + outcome. |

---

## Scope 2 — Multi-tenant isolation

Covers ADR-0002 (canonical tenant stamping) + ADR-0004 (single-tenant builder) + ADR-0005 (cross-tenant SignalR).

| ID | Item | Method | Pass criterion |
|---|---|---|---|
| 2.1 | All read endpoints filter by `ITenantContext` (no global read returning rows from other tenants). | Sample 10 random `MapGet` handlers; verify `WHERE tenant_id = @tenantId` or equivalent EF filter. | 10/10 sampled endpoints filter. Any unfiltered = P0. |
| 2.2 | `CHECK (tenant_id <> '')` migration applied to every tenant-scoped table. | Grep `CREATE TABLE` + `tenant_id` across all `.sql` migrations + verify each has CHECK. | All shipped tenant tables have CHECK. Missing = P1. |
| 2.3 | SignalR hub `OnConnectedAsync` validates tenant claim before group join (ADR-0005). | Read `PlatformHub.OnConnectedAsync`; verify `IAgentTenantResolver` consulted; verify `IHubAuditSink` records joins. | Join blocked if claim/tenant mismatch; audit sink records every join. |
| 2.4 | Audit log queries scoped by tenant; no `SELECT * FROM audit_entries` cross-tenant unless caller is `PlatformAdmin`. | Read `AuditEndpoints.cs` + `IAuditQueryService` impl. | Tenant-scoped query has `WHERE tenant_id`. Cross-tenant query gated by `PlatformAdmin` policy. |
| 2.5 | Retention purges scoped per tenant (no purge that hits other tenant rows). | Read `RetentionService` + each `IRetentionTarget.PruneAsync` impl. | All purges include tenant filter OR are explicitly cross-tenant maintenance gated by config. |

---

## Scope 3 — JWT + MFA + impersonation

Covers v1.9.2 hardening (jti, DataProtection wrap, fingerprint kid, kill `?token=`, IJtiRevocationCache).

| ID | Item | Method | Pass criterion |
|---|---|---|---|
| 3.1 | JWT signature verified server-side; algorithm pinned (no `alg:none`); issuer + audience validated. | Read `JwtTokenService.cs` + `JwtBearerOptions` setup. | `ValidateIssuerSigningKey=true`, `ValidateIssuer=true`, `ValidateAudience=true`, `ValidAlgorithms` set. |
| 3.2 | JWT `jti` enforced for refresh-token revocation; revocation cache is durable (not in-process only). | Read `IJtiRevocationCache` impl; verify Redis backing or DB persistence in production wiring. | Revocation survives restart. In-memory acceptable only for dev; prod must use Redis/DB. |
| 3.3 | `?token=` query-string acceptance is killed (token only via `Authorization: Bearer`). | Grep `MessageReceived` event handler in JWT bearer setup. | No `Query["token"]` extraction outside SignalR hub-specific path; SignalR variant scoped to `/hubs/*` only. |
| 3.4 | MFA enrollment + verification audited; bypass on lost-device requires admin recovery + audit. | Read `Asterisk.Platform.Identity/Mfa/`. Sample `MfaEnrollAsync`, `VerifyAsync`, `RecoverAsync`. | Every state transition emits audit; recovery requires elevated role. |
| 3.5 | Impersonation creates new short-lived token bound to actor + impersonated user; `actor_id` claim preserved. | Read `Asterisk.Platform.Core/Impersonation/`. Verify `IImpersonationService.StartAsync` + `StopAsync`. | Actor-id claim persists across all impersonated requests; audit emits `impersonation.start` + `.stop`. |
| 3.6 | Impersonation audit log captures: actor, target, tenant, reason, timestamp, IP. | Read `ImpersonationAuditEntry` schema + emission point. | All 6 fields present. Tampering protection via append-only constraint (Scope 4). |

---

## Scope 4 — Audit log integrity

Covers `Asterisk.Platform.Audit` package + DB storage layer.

| ID | Item | Method | Pass criterion |
|---|---|---|---|
| 4.1 | Audit writer is append-only (no UPDATE / DELETE on `audit_entries` from app code). | Grep `audit_entries` in code; verify only INSERT statements. DB role for app should not have UPDATE/DELETE on audit. | Zero UPDATE/DELETE on `audit_entries` in app SQL. DB grants verified per env. |
| 4.2 | Timestamps server-sourced via `IClock`; client-supplied timestamps rejected. | Read `IAuditService.AppendAsync` impl; verify `_clock.UtcNow` used, not `entry.Timestamp ?? _clock.UtcNow`. | `Timestamp` always overwritten server-side. |
| 4.3 | Sensitive fields redacted (passwords, tokens, secrets never serialized into `payload` JSON). | Sample 5 emission sites; check for raw secret keys in payload. | No `password`, `secret`, `token`, `apiKey` raw fields in any emission payload. |
| 4.4 | Audit emission failure does not silently drop event (logged + retried OR fail loud). | Read `IAuditService.AppendAsync` error path. | Failure logs `Asterisk.Platform.Audit.write_failed` counter + warns; no swallow. |
| 4.5 | Audit query API enforces `PlatformAdmin` for cross-tenant + `TenantAdmin` for own-tenant. | Read `AuditEndpoints.cs`. | Both policies enforced; downgrade attempt returns 403. |

---

## Scope 5 — Secrets handling

Covers webhook keys, OIDC client secrets, API keys, OAuth tokens, JWT signing keys.

| ID | Item | Method | Pass criterion |
|---|---|---|---|
| 5.1 | All secret-bearing columns wrapped via `IDataProtectionProvider.CreateProtector(...)` before persist. | Grep `Protect(` + `Unprotect(` across `Storage.Postgres` repos. | Every secret column has matching Protect/Unprotect pair. Plaintext persistence = P0. |
| 5.2 | `IDataProtectionProvider` keyring backed by DB (R5.2 ADR-0003 default), not in-process or filesystem in prod. | Read `Program.cs` data protection wiring. | `PersistKeysToDbContext` or equivalent registered in prod profile. |
| 5.3 | No plaintext secrets in `appsettings*.json` shipped. (Test fixtures exempt.) | Grep `appsettings*.json` for `Password|ApiKey|Secret|Token` with literal values. | All prod config uses placeholders + env-var override. |
| 5.4 | API key reveal-once; subsequent reads return only fingerprint/prefix. | Read `ManagementApiKeyEndpoints.cs` + `IApiKeyService`. | Initial create returns full key; subsequent GET returns hash + prefix only. |
| 5.5 | Secret rotation invalidates old value across all consumers (cache invalidation + audit emission). | Read API-key + webhook-key + OIDC-secret rotation handlers. | Each rotation emits audit + flushes relevant cache (`IJtiRevocationCache`, `IWebhookKeyCache`, etc.). |

---

## Process

1. Coordinator launches per-scope subagent OR (if low budget) one subagent runs all 5.
2. Each finding goes into `docs/security/internal-audit-YYYY-MM.md` with: ID, severity, scope, affected file, repro, recommended fix, status.
3. P0 → coordinator escalation immediate. P1 → must close before ship. P2/P3 → ticket in v1.13.x backlog.
4. Sign-off section flips PENDING → READY when P0+P1 = 0 open.
5. ZAP active scan reproducible via `scripts/run-zap-scan.sh` once a staging env is available; results appended to per-run report.

## ZAP scan note (deferred when no staging)

When staging is available:
```bash
./scripts/run-zap-scan.sh https://staging.example.com /tmp/zap-report
```
Append findings to per-run audit report under "ZAP active scan" section.
