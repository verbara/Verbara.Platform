# ADR-0008: Internal security audit baseline + checklist

**Status:** Accepted
**Date:** 2026-04-26
**Context:** R5.4 Production Validation

## Context

R5.4 closes the R5 Production Readiness Release Train. The R5 release train spec
(`docs/plans/active/2026-04-22-r5-production-readiness-release-train.md`)
originally scoped S5.4 as "Pen-test engagement + remediation cycle" with an
external vendor (2-3 week calendar bound). Post-R5.3 brainstorm (2026-04-26)
surfaced D-FORCE-1: the user does not have an external pen-test vendor
contracted and chose to start with an **internal security audit** subagent-driven.

## Decision

S5.4 is renamed **"Internal security audit + remediation"**. Approach:

- 4-5 background subagents executing in parallel, each scoped to one of:
  1. **OWASP Top 10 web** — automated baseline via OWASP ZAP Docker + manual
     review of `/management/*` and `/api/v1/admin/*` endpoints
  2. **Multi-tenant isolation** — cross-tenant leakage probe across endpoints,
     SignalR groups, audit log queries, retention purges (input ADR-0002 +
     ADR-0004 + ADR-0005)
  3. **JWT + MFA + impersonation** — token validation, refresh flow, MFA bypass
     attempts, impersonation audit completeness
  4. **Audit log integrity** — append-only enforcement, timestamp tampering,
     sensitive field redaction
  5. **Secrets handling** — `IDataProtectionProvider` wrap correctness, plaintext
     scan in config/logs/audit, key rotation tested

Tools: OWASP ZAP (baseline + active scan) + Burp Suite Community + sqlmap + manual
code review.

Output: `docs/security/internal-audit-2026-04.md` (findings table) +
`docs/security/audit-checklist.md` (permanent checklist) +
`scripts/run-zap-scan.sh` (reproducible).

Findings tracked as GitHub issues with label `security-audit-r5.4`, severity
P0/P1/P2/P3. **P0/P1 are blockers of R5.4 ship**; P2/P3 → tickets v1.13.x patches.

## Alternatives considered

- **External pen-test vendor (original spec)** — rejected for R5.4: no vendor
  contracted, no enterprise customer requesting today. Will be revisited when
  customer-driven trigger appears (R6 or ad-hoc).
- **No audit, defer to v1.13.x** — rejected: R5 release train marketing
  narrative requires security-audit attestation; defer would weaken claim
  "production-validated platform".
- **Hybrid (internal first, external later as patch)** — accepted as future path:
  external pen-test can be commissioned post-R5 ship as v1.13.x or R6 enhancement.

## Consequences

- **Positive:** No external calendar bound; R5.4 ships in 2-2.5 weeks. Subagent-
  driven approach matches R5.2/R5.3 successful patterns. Findings tracked as
  internal issues, fixable iteratively.
- **Negative:** No "redacted public security report" sales asset until external
  vendor engaged. Compliance frameworks (SOC 2, ISO 27001) typically require
  external attestation for full credit; internal audit covers due diligence
  baseline only.
- **Neutral:** `audit-checklist.md` becomes permanent artifact reused on every
  R5.x patch + future R6+ pre-ship audits.

## References

- R5.4 spec § "Track B" S5.4
- D-FORCE-1 brainstorm 2026-04-26
- OWASP Top 10 2021 (basis for checklist scope 1)
- ADR-0002 (multi-tenant isolation, input scope 2)
- ADR-0005 (cross-tenant SignalR validation, input scope 2)
