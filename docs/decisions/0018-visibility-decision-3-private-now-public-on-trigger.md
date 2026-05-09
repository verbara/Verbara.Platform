# ADR-0018: Visibility — Private Now, Public on Trigger Checklist (Decision 3)

- **Status:** Accepted
- **Date:** 2026-05-08
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - [ADR-0016 License and Rebrand to Verbara](0016-license-and-rebrand-to-verbara.md) — establishes Apache 2.0 + funnel-based revenue rationale; this ADR does NOT supersede 0016
  - [ADR-0017 Verbara Rebrand Execution](0017-verbara-rebrand-execution.md) — versioning decisions during the rebrand
  - [Web ADR-0007 Visibility Decision (mirror)](https://github.com/verbara/Verbara.Platform.Web/blob/main/docs/decisions/0007-visibility-decision-3-private-now-public-on-trigger.md) — Web-side mirror of this decision
  - [SDK ADR-0027 Stewardship Pledge](https://github.com/verbara/Verbara.Sdk/blob/main/docs/decisions/0027-stewardship-pledge-mit-commercial.md)
  - Active plan: `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md`

## Context

ADR-0016 (Accepted 2026-05-03) chose **Apache License 2.0** for this repository. The financial rationale documented there is built on the premise of public visibility:

> *"Apache yields ~2× ARR vs BSL based on legal-review-friction funnel modeling (95% vs 20% pass-through rate at enterprise legal review). Funnel modeling: 1000 GitHub visitors/month → ~3 conversions/month at $30k/customer = $1.08M ARR."*

The `LICENSE` file (Apache 2.0) was committed 2026-05-03 per ADR-0016. The README declares *"Backend for the Verbara open-core contact-center platform"* with the 4-row stack table showing Platform under Apache 2.0.

However, the GitHub repository at `github.com/verbara/Verbara.Platform` is currently **private**. Discovery during a cross-repo licensing & visibility audit (2026-05-08, originating in `Verbara.Sdk` session) surfaced this gap:

- Apache 2.0 does not legally require public source — Apache governs distribution terms; private hosting is not a license breach.
- But the strategic rationale documented in ADR-0016 — funnel-driven evaluator-to-Pro-customer conversion — cannot operate while the repository is unobservable.
- The "open-core honest" differentiator narrative (vs Twilio/Genesys closed competitors) requires evaluator-visible source.
- Public repos receive **free secret scanning + push protection** from GitHub; private repos require GitHub Advanced Security (paid) for the same protection. Currently the repository operates with reduced security posture as a side effect of being private.

Three paths forward were evaluated:

1. **Decision 1 — Publish today.** Rejected: git history has not been audited for leaked secrets, sensitive endpoints (billing, multi-tenant boundaries) have not been security-reviewed, threat model not documented. Risk of public exposure of secrets, vulnerabilities, or production-customer data is too high to accept now.

2. **Decision 2 — Stay private forever, supersede ADR-0016.** Rejected: would discard the deliberate strategic thinking captured in ADR-0016 (BSL/AGPL/proprietary all evaluated and rejected with documented reasoning). Closing source would also require renegotiating the broader narrative ("open-core honest") that distinguishes Verbara from Twilio/Genesys. The Apache 2.0 license decision remains correct; only the *execution path* needs documentation.

3. **Decision 3 — Private now, public on explicit trigger checklist.** Selected. Honors ADR-0016's economics by setting a credible path to public, while not exposing today what has not been audit-prepared. Trigger conditions are concrete and verifiable.

## Decision

This repository remains **private** until ALL trigger conditions below are met. Once all are green, the repository flips to **public** in a single coordinated change (with `Verbara.Platform.Web` per Web ADR-0007), at which point ADR-0016's Apache 2.0 economics begin operating.

### Trigger checklist (must all be ✅ before flipping)

1. **`gitleaks detect` clean across full history.** No leaked secrets, JWT signing keys, AWS credentials, `.env` content, or DB connection strings discoverable via the standard ruleset. Findings either rotated and history-rewritten or rotated and documented as historical-revoked-only.
2. **Operational Foundation closed in Web.** Per `Verbara.Platform.Web/docs/plans/active/2026-05-03-v1.14.x-operational-foundation-roadmap.md`. Implies CI/CD pipeline, error tracking, security headers, npm vulns resolved on the consumer surface.
3. **Internal security review of sensitive endpoints.** `/api/v1/billing/*`, multi-tenant boundary tests (cross-tenant access prevention), admin operations, MFA-enforcement paths. Each reviewed for authn → authz → tenant scoping → input validation → output filtering → audit log. Gaps closed before flip.
4. **Public threat model documented.** `docs/security/threat-model.md` enumerating: assets, what going-public exposes, what remains protected (the binary moat is `Pro.Licensing.LicenseGuard` ECDSA validator in the closed Pro repo), threat actors, mitigations.
5. **`LicenseGuard` not bypassable by trivial reflection / IL editing.** Tracked in Pro plan `2026-05-08-pro-licensing-eula-overhaul.md`. Tamper-resistance baseline (binary hash check at startup) shipped before flip.
6. **`verbara.io` brand setup complete.** Apex landing page, doc subdomain, contact emails (`legal/security/licensing/hello@verbara.io`) active and routed.
7. **First signed Tier 1+ customer demo.** A real paid commitment validating pricing and tier model exists, so the public-launch narrative ("$5k/yr starts here") is backed by reality, not aspiration.

### What this ADR does NOT change

- **License**: Apache 2.0 stands. ADR-0016 is not superseded.
- **Stewardship pledge**: SDK ADR-0027 unchanged.
- **Tier model**: 5-tier model from Web ADR-0006 + Tier 0.5 Developer addition (per Pro plan Phase 2 ADR, when shipped) unchanged.
- **Repository name**: `Verbara.Platform` stands.

### What this ADR does change

- **Operational status**: documented from "private with no path to public" (implicit pre-2026-05-08) to "private with explicit go-public criteria" (this ADR).
- **Cross-repo coordination**: flip is paired with `Verbara.Platform.Web` per Web ADR-0007. Both repos go public on the same day or neither does.

## Consequences

**Positive:**
- Honors ADR-0016 economics by establishing a credible path to public visibility, rather than passively contradicting it.
- Allows continued private operation during pre-launch hardening without ambiguity about the long-term plan.
- The trigger checklist forces concrete pre-launch hygiene (security review, threat model, gitleaks audit) that is good practice anyway.
- When triggered, gains free GitHub secret scanning + push protection (currently blocked by private + no GHAS).
- Aligns with the Tier 0 Community narrative documented in ADR-0016 — Tier 0 self-host requires public visibility and is not actually offered today.

**Negative:**
- Delays the funnel-launch by however long the trigger checklist takes (estimated 4-8 weeks from ADR acceptance, depending on Operational Foundation closure).
- Trigger 7 (first customer) is a business event not directly controllable by engineering; risk of dragging is real.
- Once public, ADR-0016's noted competitor-fork risk (~12-18 months engineering to clone) becomes active. Mitigation: `LicenseGuard` ECDSA in Pro remains the binary moat per ADR-0016.

**Trade-off:**
- Trades immediate funnel activation (which would be fictional anyway, since the Tier 0 Community offer cannot be honored on a private repo today) for disciplined pre-launch hardening. Acceptable.

## Alternatives considered

- **Decision 1 (publish today)**: rejected. Git history audit + security review must precede any public exposure of a backend with billing endpoints, multi-tenant DB schema, and internal admin operations.
- **Decision 2 (private forever, supersede ADR-0016)**: rejected. Would discard the legal-review-friction analysis and the open-core differentiator. ADR-0016's reasoning has not been invalidated; only its execution was incomplete.
- **Time-based public commitment** ("public by 2026-Q3 regardless"): rejected. Hard date without trigger validation invites premature flip if hardening is delayed.
- **Trigger checklist with weaker bar** (e.g., omit security review): rejected. The cost of one breach narrative post-flip exceeds the cost of a 1-2 week security review pre-flip.

## Status update

(append-only; do not modify the original ADR text above)

- **2026-05-08**: ADR Accepted. Trigger checklist active. Tracking in `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md`.

## References

- ADR-0016 (this repo) — license decision (Apache 2.0) and rebrand to Verbara
- ADR-0017 (this repo) — rebrand execution (versioning)
- Web ADR-0006 — license + 5-tier commercial strategy
- Web ADR-0007 (when shipped) — Web-side mirror of this decision
- SDK ADR-0027 — stewardship pledge
- SDK auto-memory `project_2026_05_08_licensing_audit.md` — the audit that surfaced this gap
- Active plan `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md`
