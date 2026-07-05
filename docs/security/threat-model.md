# Threat Model — Verbara Platform

**Status:** Living
**Last reviewed:** 2026-05-09
**Owners:** Verbara maintainer (security@verbara.io)
**Audience:** Public — written for enterprise security reviewers, SOC 2 auditors, evaluators considering self-host or Pro adoption.

> **Origin:** Required by [ADR-0018](../decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) Trigger 4 as a precondition to flipping this repository public. This document is **append-only at the section level** — when a threat changes, add a "Status update" entry rather than editing prior text.

---

## 1. Scope

This threat model covers **`Verbara.Platform`** — the backend composition-root and HTTP API for the Verbara open-core contact-center stack. It does **not** cover:

- Operator UI (`Verbara.Platform.Web`) — has its own threat model
- Verbara SDK (MIT) — primitives only; threat surface is per-consumer
- Verbara SDK Pro (commercial, closed-source) — `LicenseGuard` and EULA-bound enforcement live there
- Asterisk PBX runtime — trust boundary; AMI/AGI/ARI surfaces are governed by Asterisk-side configuration, not by this codebase

Where these adjacent systems are referenced, this document states only what `Verbara.Platform` assumes about their behavior.

## 2. Assets

What we are protecting and why an attacker would want it.

| # | Asset | Sensitivity | Why it matters |
|---|---|---|---|
| A1 | Tenant-isolated business data (calls, conversations, CDR, queues, agents, customers) | High | Cross-tenant leak destroys multi-tenant trust; PII + PCI exposure if leaked publicly |
| A2 | JWT signing keys (RS256/ES256 private keys + DataProtection-wrapped HMAC fallback) | Critical | Forging a JWT bypasses authn entirely; rotation is invasive |
| A3 | Audit log (`audit_entries`) integrity | High | Tampering or deletion defeats SOC 2, legal hold, compliance posture |
| A4 | Stored secrets (webhook keys, OIDC client secrets, API keys, OAuth refresh tokens) | High | Direct credential reuse against external integrations or against this Platform |
| A5 | Pro license keys (consumer-side `LicenseGateMiddleware` config) | Medium | Bypassing the gate gives Pro features without payment; ECDSA validator in `Verbara.Sdk.Pro.Licensing` is the binary moat — see §5 "What remains protected" |
| A6 | Impersonation tokens (admin-on-behalf-of-user sessions) | Critical | Misuse equals undetected privilege escalation across tenants |
| A7 | MFA enrollment material (TOTP shared secrets, recovery codes) | High | Recovery without audit collapses second-factor protection |
| A8 | Realtime event stream (SignalR hubs `/hubs/*`) | Medium | Subscribing to another tenant's hub group leaks live operational state |
| A9 | Database connection string + DataProtection keyring | Critical | Owns A1–A8 transitively |
| A10 | DBA-level Postgres role used by retention purger | Medium | Privileged DELETE on `audit_entries` (the only legitimate DELETE path); misuse erases history |

## 3. What going public exposes

When this repository flips to public per ADR-0018, the following becomes world-readable:

- **Full source code** of all 31 src packages — every endpoint handler, every DI registration, every SQL migration
- **Test corpus** — request/response shapes, fixtures (verified clean of true secrets per `docs/research/2026-05-08-gitleaks-audit.md`)
- **Database schema** — all `.sql` migrations including `tenant_id` constraints, audit table layout, index strategy
- **Deployment artifacts** — `docker-compose.full.yml`, sample configs, ZAP scan script
- **Operational runbooks** — `first-deploy.md`, `first-realistic-demo.md`, capacity-planning notes
- **ADRs and security audit history** — the very documents you are reading

This means an attacker can:

1. Read every authentication and authorization code path before probing — **no security through obscurity**
2. Identify NuGet versions and chain known CVEs against unpatched deployments
3. Generate realistic-looking traffic by replaying the documented request shapes
4. Locate any logic-vs-policy gap (e.g. an endpoint that lacks `RequireAuthorization`) by reading rather than fuzzing

This is a deliberate trade chosen in [ADR-0016](../decisions/0016-license-and-rebrand-to-verbara.md): public source maximizes adoption funnel and enables third-party security review. The defensive posture must therefore stand on **strength, not concealment**.

## 4. What remains protected

Going public does **not** weaken these:

| Control | Where it lives | Why public source does not bypass it |
|---|---|---|
| Pro feature gating | `Verbara.Sdk.Pro.Licensing.LicenseGateMiddleware` (commercial repo, closed source) | Source not published; only the contract (interface + middleware shape) is shipped via NuGet |
| ECDSA license-key validation | `Verbara.Sdk.Pro.Licensing.LicenseTrustAnchor` (Pro v2.2.0-pro+) — embedded P-256 public key with `OfficialPublicKeyFingerprintSha256` constant | Forging a license requires the Verbara-controlled private key. Reading the public key from a decompiled Pro DLL does not enable forgery. |
| Production secrets (JWT signing keys, DB credentials, webhook keys) | Operator's environment / KMS / DataProtection keyring — never in this repository | Source review reveals **how** secrets are consumed, never their values |
| Customer data | Operator's database — never in this repository | Source review enumerates schema, not content |
| Trademark "Verbara" | Brand-use evidence kit; future trademark filing on first-revenue trigger | A fork can rename and run; public source does not transfer the trademark |
| Pro EULA enforcement | Combination of license-key signature + EULA contract terms | Removing the gate by source patch produces an unlicensed binary, which is a contract violation, not a technical defeat |

The deliberate design is **moat-by-binary, not moat-by-source**: the open-source Platform implements an interface that any compliant Pro DLL satisfies. Anyone can swap a fake `LicenseGateMiddleware`, but doing so produces a derivative work outside the Apache 2.0 grant for the Pro DLL (which they cannot redistribute anyway, since they do not have its source) and outside the EULA (which they have not accepted).

## 5. Threat actors

| Actor | Capability | Motivation | Treated as |
|---|---|---|---|
| TA1 — Anonymous internet attacker | Network reach to public endpoints; can read source post-flip | Opportunistic exploitation, ransomware staging | Primary threat |
| TA2 — Tenant operator (legitimate customer) | Valid credentials; can issue any operator-level API call | Curious; misconfigured; compromised account | Scoped threat — must not reach other tenants |
| TA3 — Tenant agent (least-privileged user) | Valid agent credentials; UI access only | Curiosity; misuse of impersonation pattern | Scoped threat — should not see operator surfaces |
| TA4 — Insider (Verbara maintainer with repo access) | Source + secret-store access; commit privilege | Mistakes; rare malicious intent | Out of scope for technical mitigation; covered by audit log + commit signing |
| TA5 — Competitor / forker | Source read; legal team; can run a clone | Market substitution | Treated by trademark + Pro binary moat, not by source obscurity |
| TA6 — Supply-chain attacker | Compromise of a NuGet dependency or a CI runner | Inject backdoor at build time | Defended by `dependency-review` + Dependabot + signed releases (planned) |
| TA7 — Asterisk-side attacker | AMI / AGI / ARI access on the operator's PBX | Lateral movement from PBX into Platform | Boundary-of-trust threat — see §6 STRIDE/Spoofing |

## 6. Threats (STRIDE per asset)

For each STRIDE category, the salient threat scenarios with current mitigation status. Items reference the canonical [`audit-checklist.md`](audit-checklist.md) by Scope.Item identifier.

### 6.1 Spoofing (S)

| Threat | Asset | Mitigation | Status | Reference |
|---|---|---|---|---|
| TA1 forges a JWT with `alg:none` or HMAC-with-public-key trick | A2 | Algorithm pinned via `JwtBearerOptions.ValidAlgorithms`; `ValidateIssuerSigningKey=true`; asymmetric RS256/ES256 | ✅ Verified | Audit Scope 3.1 |
| TA1 replays a stolen JWT after logout | A2, A6 | `IJtiRevocationCache` enforces `jti` denylist on every request | ⚠️ In-process default; durable Redis variant ships in `Verbara.Platform.Identity.Redis` (R5.1) — operators on multi-pod **must** opt in | Audit Scope 3.2; finding [MFA-007](internal-audit-2026-04.md#audit-2026-04-mfa-007--mfa-pending-cache--jti-revocation-are-in-process-by-default-in-identity-p2-scope-3) |
| TA1 connects to SignalR hub with another tenant's JWT | A8 | `PlatformHub.OnConnectedAsync` validates tenant claim via `IAgentTenantResolver`; `IHubAuditSink` records every join | ✅ Verified | Audit Scope 2.3, [ADR-0005](../decisions/0005-cross-tenant-signalr-subscription-validation.md) |
| TA7 (Asterisk-side) injects events claiming to be from another tenant | A1, A8 | Asterisk integration is single-tenant per deployment; tenant stamping happens at ingress in Platform, not at Asterisk | ✅ By-design | [ADR-0002](../decisions/0002-tenant-stamping-pipeline-end-to-end.md) |

### 6.2 Tampering (T)

| Threat | Asset | Mitigation | Status | Reference |
|---|---|---|---|---|
| TA1 modifies request to access cross-tenant resource via path traversal | A1 | All read endpoints filter by `ITenantContext`; path-supplied tenant ID ignored when conflicting with claim | ✅ Verified (10/10 sample) | Audit Scope 2.1 |
| TA2 calls another tenant's endpoint by guessing IDs | A1 | `CHECK (tenant_id <> '')` migrations on every tenant-scoped table + WHERE-tenant_id on every query | ✅ Verified across shipped tables; 3 `analytics_interval_snapshots` tables flagged for confirmation | Audit Scope 2.2; finding [MT-005](internal-audit-2026-04.md#audit-2026-04-mt-005--analytics_interval_snapshots-3-table-check-constraints-p3-adr-0002) |
| TA2 mutates `audit_entries` to hide their own action | A3 | App SQL is INSERT-only; only `DeleteOlderThanAsync` issues DELETE (tenant-scoped + time-bounded) | ✅ Verified by code review | Audit Scope 4.1; finding [AUDIT-006](internal-audit-2026-04.md#audit-2026-04-audit-006--audit-delete-limited-to-retention-purge--verified-p3-info) |
| TA2 forges a future-dated audit entry to mask sequencing | A3 | `Timestamp` always overwritten with `IClock.UtcNow` server-side | ✅ Verified | Audit Scope 4.2 |
| TA1 patches local Pro DLL to bypass `LicenseGateMiddleware` | A5 | Acknowledged residual exposure (see §8). The bypass produces a non-compliant deployment outside EULA grant; technical detection deferred (basic `LicenseTrustAnchor` shipped in Pro v2.2.0-pro; binary hash check at startup is on the Pro v2.3+ roadmap, not yet shipped) | 🟡 Partial | ADR-0018 Trigger 5 status |

### 6.3 Repudiation (R)

| Threat | Asset | Mitigation | Status | Reference |
|---|---|---|---|---|
| Operator denies performing an admin action | A3 | Every `/management/*` mutation emits `IAuditService.AppendAsync` with actor + tenant + outcome | ✅ Verified (sample) | Audit Scope 1.10 |
| Admin denies starting an impersonation session | A6 | Impersonation start/stop emits audit with actor, target, tenant, reason, IP | ✅ Verified | Audit Scope 3.5–3.6, finding [IMP-008](internal-audit-2026-04.md#audit-2026-04-imp-008--impersonation-audit-captures-actor--target--reason--ip--verified-p3-info) |
| Audit emission fails silently during an attack | A3 | Failure logs `Verbara.Platform.Audit.write_failed` counter + warns; no swallow | ✅ Verified | Audit Scope 4.4 |
| Insider TA4 force-pushes to rewrite audit doc history | All docs | Branch protection on `main`; commit signing required; ADR append-only convention | ⚠️ Branch protection settings TBD pre-flip; tracked as Phase 2.2 of visibility-decision plan | Visibility plan §2.2 |

### 6.4 Information disclosure (I)

| Threat | Asset | Mitigation | Status | Reference |
|---|---|---|---|---|
| TA1 retrieves stored OAuth refresh tokens or webhook keys via SQL injection | A4 | All queries parameterized via Dapper `@param` or EF `FromSql`; no string-concat SQL on user input | ✅ Verified by grep | Audit Scope 1.5 |
| TA1 reads plaintext secrets from DB after stealing a backup | A4 | Every secret column wrapped via `IDataProtectionProvider`; keyring backed by DB per [ADR-0003](../decisions/0003-dataprotection-key-persistence-strategy.md) | ✅ Verified | Audit Scope 5.1–5.2 |
| TA2 reads another tenant's audit log | A3 | Tenant-scoped query filters by `WHERE tenant_id`; cross-tenant query gated by `PlatformAdmin` policy | ✅ Verified | Audit Scope 2.4, 4.5 |
| TA1 reads dev-default credentials from shipped `appsettings.Development.json` | A4 | Development file ships with `admin:admin` placeholder + `platform_internal_secret` `ServiceKey`; **fix pending** in v2.0.x | 🟡 Tracked | Finding [CFG-003](internal-audit-2026-04.md#audit-2026-04-cfg-003--plaintext-placeholder-credentials-in-appsettingsdevelopmentjson-p2-owasp-a05--scope-5) |
| TA1 captures a JWT from server access logs because it was passed in `?token=` | A2 | Bearer-only acceptance enforced; SignalR `?access_token=` accepted only on `/hubs/*` (fix pending) | 🟡 Tracked | Finding [AUTH-002](internal-audit-2026-04.md#audit-2026-04-auth-002--token-query-string-accepted-globally-instead-of-scoped-to-hubs-p2-owasp-a02--a07) |
| API key revealed multiple times after creation | A4 | Initial create returns full key; subsequent reads return hash + prefix only | ✅ Verified | Audit Scope 5.4 |
| Sensitive payload field (`password`, `token`, `secret`) leaks via audit log payload | A3 | Sample inspection of 5 emission sites confirms no raw secret keys in payloads | ✅ Verified | Audit Scope 4.3 |

### 6.5 Denial of service (D)

| Threat | Asset | Mitigation | Status | Reference |
|---|---|---|---|---|
| TA1 brute-forces login endpoint | A2 | `/api/auth/login` rate-limited per IP + per username; lockout after N failures; emits `auth.login.failed` audit | ✅ Verified | Audit Scope 1.9 |
| TA1 floods SignalR hub with connections | A8 | Connection cap per-pod; backpressure via SignalR hub options; horizontal scale via Redis backplane (implementation detail, no dedicated ADR) | ✅ By-design | — |
| TA1 exhausts connection pool with slow requests | A9 | Npgsql pool tuning per [ADR-0015](../decisions/0015-npgsql-datasource-sharing-strategy.md) (sharded pools, R5.5 v1.14.5+) | ✅ By-design | — |
| TA6 (supply chain) ships a NuGet update with a runtime hang | All | Dependabot + `dependency-review` + version pin in `Directory.Packages.props`; explicit upgrade gate before merge | ✅ Process | — |

### 6.6 Elevation of privilege (E)

| Threat | Asset | Mitigation | Status | Reference |
|---|---|---|---|---|
| TA3 (agent) accesses operator-only endpoint | A1, A2 | Every `/management/*` endpoint has `RequireAuthorization` + correct permission policy (`PlatformAdmin` or specific permission); no `MapGet` without policy in management group | ✅ Verified | Audit Scope 1.1 |
| TA2 escalates to `PlatformAdmin` by editing claims | A2 | JWT signature validated; claims authoritative; no client-supplied role accepted | ✅ Verified | Audit Scope 3.1 |
| TA2 bypasses MFA via lost-device recovery | A7 | MFA recovery requires admin elevation + audit emission per Audit Scope 3.4 | ✅ Verified | Audit Scope 3.4 |
| TA4 insider rotates a JWT signing key without audit trail | A2, A3 | Rotation handlers emit audit + flush relevant cache; `IDataProtectionProvider` rotation visible in audit timeline | ✅ Verified | Audit Scope 5.5 |
| Privilege escalation via JTI cache loss after restart in single-instance deployments using the in-memory default | A2 | Documented operator responsibility; hardening planned: fail-loud startup check when in-memory cache + `Production` env | ⚠️ Tracked | Finding [MFA-007](internal-audit-2026-04.md#audit-2026-04-mfa-007--mfa-pending-cache--jti-revocation-are-in-process-by-default-in-identity-p2-scope-3) |

## 7. Cross-cutting controls

These mitigations cover multiple threats and are not bound to a single STRIDE row.

| Control | Coverage | Where |
|---|---|---|
| `IDataProtectionProvider` keyring backed by DB | A2, A4, A9 | `Program.cs` data-protection wiring, [ADR-0003](../decisions/0003-dataprotection-key-persistence-strategy.md) |
| Append-only `audit_entries` (no app UPDATE/DELETE) | A3, repudiation | Audit Scope 4.1 |
| Tenant-scoping at every read/write boundary | A1, A8 | Audit Scope 2.1–2.5; [ADR-0002](../decisions/0002-tenant-stamping-pipeline-end-to-end.md), [ADR-0005](../decisions/0005-cross-tenant-signalr-subscription-validation.md) |
| `dotnet list package --vulnerable` clean cross-repo | TA6 | R5.4 ship gate; v1.14.x patch train confirmed clean |
| Branch protection + commit signing on `main` | TA4, TA6 | To be configured pre-flip (visibility plan Phase 2.2) |
| Public security disclosure channel | All | `security@verbara.io`; `SECURITY.md` to be added pre-flip |
| Secret scanning + push protection (free on public repo) | TA1, TA4 | Auto-enabled by GitHub on flip per [ADR-0018](../decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) §Consequences |
| `gitleaks detect` history scan | TA1 | Run 2026-05-08; 0 true-positives; documented in `docs/research/2026-05-08-gitleaks-audit.md` |

## 8. Open risks and residual exposure

Honest accounting of what is **not** fully mitigated and how that risk is accepted.

| Risk | Severity | Acceptance rationale | Re-evaluation trigger |
|---|---|---|---|
| `LicenseGateMiddleware` bypassable by source patch of the open Platform code (swapping the Pro DLL for a stub) | Medium | The bypass produces an unlicensed binary, which is a contract violation under the EULA. Detection is non-technical (commercial enforcement); the binary moat is the ECDSA license-key validator in the closed Pro DLL, not the gate's source. | First incident of detected commercial bypass; or shipping `LicenseTrustAnchor` startup hash check (Pro v2.3+ roadmap) |
| In-memory default for `IJtiRevocationCache` survives single-pod restart but loses revocation across replicas; operator must opt into Redis | Medium | Documented; R5.1 shipped the durable variant; production deployments at scale (>1 pod) are expected to opt in. Fail-loud guard tracked as v2.0.x ticket [MFA-007](internal-audit-2026-04.md#audit-2026-04-mfa-007--mfa-pending-cache--jti-revocation-are-in-process-by-default-in-identity-p2-scope-3). | Shipping the fail-loud guard; or first reported revocation-bypass incident |
| `appsettings.Development.json` ships with placeholder credentials in container image | Low | Production wiring uses env-var override; `ASPNETCORE_ENVIRONMENT=Production` is the deploy default. v2.0.x ticket [CFG-003](internal-audit-2026-04.md#audit-2026-04-cfg-003--plaintext-placeholder-credentials-in-appsettingsdevelopmentjson-p2-owasp-a05--scope-5) tracks removal. | First customer report of accidental Dev-mode prod deploy; or the ticket lands |
| `?access_token=` query-string acceptance not yet scoped to `/hubs/*` only | Low | Tokens still validated; risk is leakage via referrer/log, not bypass. v2.0.x ticket [AUTH-002](internal-audit-2026-04.md#audit-2026-04-auth-002--token-query-string-accepted-globally-instead-of-scoped-to-hubs-p2-owasp-a02--a07). | Ticket lands |
| OWASP ZAP active scan deferred (no staging environment) | Low | Reproducible script `scripts/run-zap-scan.sh` ready; runs on first staging stand-up. Compensating control: code-review-based audit per `audit-checklist.md` | Staging environment reachable |
| Security headers (HSTS, CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy) — verification gap | Low | Likely added by reverse proxy in production; not auditable from the repository alone. Tracked as next-quarter improvement. | Documenting expected proxy + adding integration test |
| Competitor fork once public | Strategic, not security | Acknowledged in [ADR-0016](../decisions/0016-license-and-rebrand-to-verbara.md); ~12-18 months engineering to clone; trademark + Pro binary moat are the defensive surfaces, not source license | First credible fork emerges |

## 9. Out of scope

The following are explicitly outside this threat model:

- **Asterisk PBX hardening** — covered by Asterisk-side configuration; `Verbara.Sdk` consumers are responsible for AMI/AGI/ARI auth posture per `docs/guides/high-load-tuning.md` in the SDK
- **Operator-supplied infrastructure** — Postgres hardening, network ACLs, TLS termination, KMS configuration; threat model assumes operator follows their own infra security baseline
- **Physical / supply-chain attacks on Verbara maintainer machines** — covered by general developer hygiene; not a code-level concern
- **Social-engineering attacks against `security@verbara.io`** — process control, not technical
- **Webhook receiver-side security** — `Verbara.Platform` signs outbound webhooks; the receiver is responsible for verifying signatures

## 10. Cross-references

- [ADR-0016 License and Rebrand to Verbara](../decisions/0016-license-and-rebrand-to-verbara.md) — chose Apache 2.0; established Pro binary moat as the engineering defense
- [ADR-0018 Visibility Decision (Decision 3)](../decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) — required this document as Trigger 4
- [Audit Checklist (permanent template)](audit-checklist.md) — the canonical source for the per-Scope criteria referenced throughout this document
- [Internal Security Audit 2026-04](internal-audit-2026-04.md) — most recent run; status of P2/P3 findings referenced here
- [Gitleaks audit 2026-05-08](../research/2026-05-08-gitleaks-audit.md) — history scan; 0 true-positives
- ADR-0002 — canonical tenant stamping (multi-tenant integrity)
- ADR-0005 — cross-tenant SignalR subscription validation
- Web mirror: [Verbara.Platform.Web ADR-0007](https://github.com/verbara/Verbara.Platform.Web/blob/main/docs/decisions/0007-visibility-decision-3-private-now-public-on-trigger.md) — the Web repository's parallel visibility decision

## Status updates

(append-only; do not modify the original document text above)

- **2026-05-09**: Initial version created. Closes ADR-0018 Trigger 4. Status delta from drafting: 3 ✅, 4 ⚠️/🟡 (in-process JTI default, dev-config secrets, query-string token scoping, ZAP scan deferral), 0 ❌. All 🟡 items are tracked as v2.0.x tickets in `internal-audit-2026-04.md` (originally filed as v1.13.x in 2026-04 — re-scoped to v2.0.x post-rebrand).

- **2026-05-09 (later — v2.0.1 ships closures for both P0s and all 4 P1s)**: All 6 grep-able-from-source findings raised in the pre-public review are now closed in code. Updates by section:
  - **§6.2 Tampering** — the row claiming "path-supplied tenant ID ignored when conflicting with claim — ✅ Verified (10/10 sample)" was factually superseded earlier today. NOW IT IS ✅ **Verified-fixed** by `TenantBoundaryValidationMiddleware` (`src/Verbara.Platform.Api/Middleware/TenantBoundaryValidationMiddleware.cs`, commits `4718a870` + `3a90300b`). Bare `AdminOnly` surfaces (`/admin/users|queues|agents|teams|audit`) reject `X-Tenant-Id` headers that conflict with the JWT `tid` for Customer-tenant Admins; legitimate Platform/Partner cross-tenant access continues to work via the `TenantType.Platform`/`TenantType.Partner` allow path. 5 RED→GREEN regression tests cover the four `/admin/*` surfaces; 5 control tests guard against legitimate-flow breakage.
  - **§6.3 Repudiation** — the row "Operator denies performing an admin action — ✅ Verified" was sound for the management surface but missed billing entirely. NOW IT IS ✅ **Verified-comprehensive** by `BillingAuditEventTypes` + 8 audit emissions added to `ManagementBillingEndpoints` (commit `2b83604a`). Money-mutating operations (rate-card create/update/delete, invoice generate/issue/pay, quota update, dunning pause) now emit `IAuditService.AppendAsync` with actor + tenant + before/after change set. `PayInvoice` records both `payment_status_before/after` and `tenant_status_before/after`.
  - **§6.4 Information-Disclosure** — the OIDC client-secret plaintext issue (`PREPUB-2026-05-09-ADMIN-001`) is now ✅ **Verified-fixed** by `IDataProtectionProvider`-wrapped persistence in `PostgresTenantAuthConfigStore` + idempotent `OidcClientSecretEncryptionMigrator` for existing rows + redacted response DTO `TenantAuthConfigResponse` (carrying only `OidcClientSecretSet: bool` + 8-hex SHA-256 fingerprint, never the raw value). Commit `23409c55`. 4 Api regression tests + 4 Storage.Postgres Testcontainers tests added.
  - **§6.6 Elevation-of-privilege** — two new rows are now ✅ **Verified-fixed** in code (rows added implicitly via this Status update; will be folded into the next top-level threat-model revision):
    - **MFA admin tenant-scoping** — `?targetTenant=` on `/management/mfa/users/*` now requires the caller to be `TenantType.Platform` OR the target to be in the caller's hierarchy via `IsTenantInCallerHierarchyAsync`. Foreign-hierarchy attempts emit `MfaPrivilegeEscalationAttempted` audit event and return 403. Commit `baa7aaef`. 5 regression tests.
    - **Management API key permission enforcement** — `PlatformAdminAuthorizationHandler` no longer short-circuits on `key_type=management`. The handler now reads the API key's `scopes` array and succeeds iff the requested `requirement.Permission` is contained. Legacy `platform:*` wildcard kept working through v2.0.x patches; deprecation v2.1.0; removal v3.0.0 per [ADR-0019](../decisions/0019-scope-aware-management-api-keys.md). Commit `c35a0d17`. 6 regression tests including back-compat smoke against `JwtKeyEndpoints.RotateKey`.
  - **Implication for the visibility flip**: ADR-0018 Trigger 3 status flips from ❌ BLOCKED back to ✅ **GREEN** as of v2.0.1. See ADR-0018 Status update of even date. Trigger dashboard: 6/7 GREEN (1, 2, 3, 4, 6, 7); 1/7 PARTIAL (5 — Pro v2.3.x execution).

- **2026-05-09 (earlier — corrections from deeper Trigger 3 audit)**: A focused pre-public security review of 60 endpoints across the four Trigger 3 families (`2026-05-09-pre-public-security-review.md`) raised **2 P0 + 4 P1** findings that supersede claims made above. Specifically:
  - §6.2 Tampering row *"path-supplied tenant ID ignored when conflicting with claim — ✅ Verified (10/10 sample)"* is **factually superseded**. The 2026-04 audit's 10-endpoint sample passed, but the deeper review found that on `/admin/users|queues|agents|teams|audit` (handler `AdminEndpoints.GetTenantId` at `src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs:589-595`) the `X-Tenant-Id` header populates `context.Items["TenantId"]` BEFORE the JWT-bearer event runs, and the JWT step then only sets the tenant *if not already present*. Result: **PREPUB-2026-05-09-MT-001 (P0)** — any tenant Admin can read another tenant's users + agent SIP passwords by setting `X-Tenant-Id` to the victim's id. Existing `RequireRole("Admin")` does not pin the caller to their own tenant.
  - §6.4 Information-Disclosure does not currently list **PREPUB-2026-05-09-ADMIN-001 (P0)** — `tenant_auth_config.oidc_client_secret` is persisted plaintext (`Migrations/001_InitialSchema.sql:109`, no `IDataProtectionProvider` wrap in `PostgresTenantAuthConfigStore`) and returned plaintext by `GET /admin/auth/config`. This is exactly the situation that audit-checklist Scope 5.1 grades as P0.
  - Four P1 findings (MFA admin tenant-scoping bypass, three audit-emission gaps on billing mutations, management API key short-circuiting `PlatformAdminRequirement`) are detailed in the new review doc. Each is grep-able from source the moment this repository goes public.
  - **Implication for the visibility flip:** ADR-0018 Trigger 3 status reverts to ❌ **BLOCKED** (was projected 🟡 PARTIAL pre-audit). Code remediation in v2.0.x patch train is required before flip. Triggers 5 and 7 decisions can proceed in parallel but are no longer the bottleneck — code is.
  - This Status update **does not edit** the §6.2 Tampering row above; readers should treat that row as accurate-at-publication-time and consult this update + the pre-public review doc for current state. A future revision (post-fix) will publish a new top-level threat-model version with the fixes verified in line.

- **2026-05-23 (image-public is orthogonal to repo-public — ADR-0023 Accepted)**: this threat model's §3 "What going public exposes" is **repository-flip** focused (ADR-0018 Trigger 4 framing). It does NOT cover the *image-visibility* question, which has its own decision train [ADR-0023](../decisions/0023-publishing-non-aot-microservices.md) (Accepted 2026-05-23). Status update addendum:
  - **Image-public is already active** since the v2.4.1 ship (2026-05-21). All 5 platform packages (`api`, `realtime`, `renderer`, `mail`, `web`) on `ghcr.io/verbara/platform/*` are anonymously pullable today via `docker pull` (verified 2026-05-23 after `docker logout ghcr.io`). This predates and is independent of the future repo-flip.
  - **§4 "What remains protected" continues to hold under image-public** because the crown-jewel Pro IP (Dialer / Analytics / CallAnalytics / AgentAssist / EventStore / Routing) ships ONLY in the Native-AOT `api` image and is recoverable only via native reverse-engineering (IDA Pro / Ghidra / radare2 + human-hour-grade work). Verified today: `file publish/Verbara.Platform.Api` → `ELF 64-bit LSB pie executable, x86-64, ..., stripped`; `ls publish/Verbara*.dll | wc -l` → 0. The non-crown-jewel Pro plumbing (Push / Cluster / MultiTenant / Licensing / Storage.Common) ships as IL in Realtime/Renderer/Mail, but is textbook-pattern code with no competitive moat; ECDSA-P256 license validation is cryptographically safe to ship as IL by Kerckhoffs's principle (private signing key never leaves Verbara license-issuance infrastructure).
  - **§4 row "ECDSA license-key validation"** — now reinforced by a build-guard (`BanCrownJewelProInNonAotMicroservices`, [Directory.Build.props:67-80](../../Directory.Build.props#L67)) that fails the build if any crown-jewel Pro package is referenced by a non-AOT microservice project. The classification is machine-enforced, not just documented.
  - **§6.2 Tampering row "TA1 patches local Pro DLL"** (status was 🟡 Partial) — additional defense layer added since the row was written: image-digest binding ([ADR-0011](../decisions/0011-auth-write-deferral.md)) means tampered images get a new manifest digest that won't match the `AuthorizedImageDigests` in any Verbara-issued license. A patched binary thus stays 402-gated even with a "valid" `.lic` file. The bypass is now technically detected at startup (`Verbara.Sdk.Pro.Licensing.ContainerImageDigest.ReadFromEnvironment()` vs the license's claim) AND contractually enforced by EULA. Severity reduces from medium to low; status moves to ✅ Verified-defended.
  - **§5 Threat actors** — TA5 (competitor / forker) capability extends to "image-pull + decompile the IL surface". Mitigation unchanged from the existing row: the crown-jewels are AOT-only; non-crown-jewel IL is textbook-pattern. Treated by trademark + Pro binary moat, not by image-access control.
  - **§8 Open risks** — the "LicenseGateMiddleware bypassable by source patch" row predates ADR-0011 Layer C. Effective new state: a source patch + rebuild produces a new image digest that won't match the license-claim authorized list → bypass works only for OSS-mode features (which are free anyway). The economic incentive for bypass collapses; technical detection via Layer C makes "I ran the genuine image" verifiable by digest. Re-evaluation trigger row updates to "first incident of detected commercial bypass" (which now requires forging Layer-A native-RE + Layer-B ECDSA + Layer-C digest binding simultaneously).
  - **Cross-reference**: full reasoning + counter-arguments rejected in [`docs/research/2026-05-23-pro-ip-exposure-deep-analysis.md`](../research/2026-05-23-pro-ip-exposure-deep-analysis.md). The verdict is "stay public" — repository visibility (ADR-0018) and image visibility (ADR-0023) are independent decision trains and remain so.
  - **Visibility-regression monitor**: added [.github/workflows/visibility-monitor.yml](../../.github/workflows/visibility-monitor.yml) — daily check that all 5 platform packages remain `public` via `gh api` + anonymous `docker pull`. Fail-loud if any flips.
  - This Status update does NOT edit §3 or §4 inline. Readers should treat the existing text as accurate-at-publication-time and consult this update for the image-public addenda. A future top-level revision (when ADR-0018 trigger checklist closes and the repo flip happens) will fold both repo-public and image-public threats into a unified §3.
