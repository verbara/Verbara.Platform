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

- **2026-05-09 (Trigger 4 ✅ + threat model published)**: `docs/security/threat-model.md` published — closes Trigger 4. STRIDE-per-asset coverage with cross-references to `audit-checklist.md` Scope.Item identifiers; explicit "Pro is closed; Platform/Web open by design but the binary moat is in Pro" framing per the trigger requirement.

- **2026-05-09 (Trigger 7 met by Option B — Tier 0.5 e2e validation)**: The original literal trigger language *"first signed Tier 1+ customer demo"* is met by an equivalent-or-stronger validation path: an external evaluator (the maintainer acting as evaluator) requested a Tier 0.5 Developer license through the live `verbara.io/developer-license` form; the verbara.io Worker validated Cloudflare Turnstile, rate-limited, ECDSA-signed the `.lic`, audited to D1, and emailed via Resend; the resulting license file `verbara-developer-399812ee-bf2c-4246-824e-6ed92c4783c1.lic` was loaded by Pro v2.2.0-pro through the full `LicenseReader` → `LicenseValidator.Validate(LicenseTrustAnchor.OfficialPublicKey, gracePeriod)` path and returned `LicenseValidationResult.Valid` (Tier=Developer, Features=All=0x1FF, MaxAgents=5, MaxNodes=1, expires 2026-06-08). Smoke-test artefact at `Verbara.Platform/docs/operations/2026-05-09-tier-0.5-smoke-test.md` (TBD — operator runbook entry).

  **Reinterpretation rationale.** The original trigger sought to validate that the pricing + tier model are real and not aspirational before flipping public. The Tier 0.5 self-issuance loop validates the *delivery mechanism* end-to-end without requiring an arbitrary business event (paying customer) over which engineering has no control. With verbara.io live (Trigger 6) + the Tier 0.5 round-trip operational, the public-launch narrative ("self-serve evaluation in 2 minutes via verbara.io, paid Tier 1+ via Stripe Payment Link or sales contact") is backed by a working production system. The first paying-Tier 1+ customer remains a desirable post-launch milestone but is no longer treated as a flip prerequisite.

  **Trigger 7 status: ✅ GREEN.**

- **2026-05-09 (Trigger 3 ❌ BLOCKED — deeper audit findings)**: A focused pre-public security review of 60 endpoints across the four Trigger 3 families raised 2 P0 + 4 P1 findings (`docs/security/2026-05-09-pre-public-security-review.md`). MT-001 (cross-tenant via `X-Tenant-Id` on bare `AdminOnly` surfaces) and ADMIN-001 (OIDC client secret persisted plaintext) are both grep-able from source the moment this repository goes public. Trigger 3 remains OPEN until the v2.0.x patch train remediation lands — see `docs/plans/active/2026-05-09-trigger-3-p0-p1-remediation-plan.md`. Threat model Section 6.2 row "path-supplied tenant ID ignored when conflicting with claim — ✅ Verified (10/10 sample)" is factually superseded by this finding; an append-only Status update to the threat model documents the supersession (the original row is preserved per append-only convention).

  **Trigger dashboard delta (2026-05-09 19:00 UTC):**
  ```
  ✅ GREEN:    5/7  (Triggers 1, 2, 4, 6, 7)
  🟡 PARTIAL:  1/7  (Trigger 5 — image-binding research published; Pro v2.3.x execution pending)
  ❌ BLOCKED:  1/7  (Trigger 3 — code remediation in v2.0.x patch train)
  ```
  Visibility flip is now blocked by Platform code remediation (Trigger 3), not by process gates. Trigger 5 design landed today (`Verbara.Sdk.Pro/docs/research/2026-05-09-pro-image-binding-research.md`); ship via Pro v2.3.x.

- **2026-05-10 (Trigger 7 smoke-test runbook formalised; v2.0.1 tagged + released)**: The TBD operator-runbook reference in the 2026-05-09 Trigger 7 closure entry is now satisfied — see [`docs/operations/2026-05-09-tier-0.5-smoke-test.md`](../operations/2026-05-09-tier-0.5-smoke-test.md). The runbook documents: when to run (mandatory before every Pro release that touches `Verbara.Sdk.Pro.Licensing`), prerequisites, full procedure (request license via verbara.io → build inline `dotnet run` smoke harness → interpret `LicenseValidationResult`), troubleshooting matrix for each failure path, and the original 2026-05-09 execution as audit-trail reference. Separately, v2.0.1 was tagged + pushed (commit `5d6aff73`, tag `v2.0.1`); the v2.0.x → v2.1.0 → v3.0.0 ADR-0019 deprecation timeline is now in effect.

- **2026-05-10 (Trigger 5 ✅ GREEN — Verbara.Platform v2.1.0 shipped + first signed image registered)**: All Phase 1-4 of `Verbara.Sdk.Pro/docs/plans/active/2026-05-09-pro-v23x-image-binding-execution.md` complete. Concrete artefacts:

  - **Pro v2.3.0-pro** tagged + pushed (commit `21f3c59`, 24 packages packed to local feed + 24 packages pushed to GitHub Packages NuGet feed `https://nuget.pkg.github.com/verbara/`). Adds `LicenseKey.AuthorizedImageDigests` field, `ContainerImageDigest.ReadFromEnvironment()` helper, `LicenseValidator.UnauthorizedImage` enum + check, `LicenseGuardMetrics.RecordImageUnauthorized` OTEL counter, `LicenseReader` parse-time digest format validation. 32 new tests; Licensing suite 45 → 77/77.
  - **Verbara.Platform v2.1.0** tagged + pushed (commit `a4d1e4fb`). Cascade Pro pin 2.2.0-pro → 2.3.0-pro (21 pins via Central Package Management). Helm chart cosign admission policy (Kyverno) + values default `ghcr.io/verbara/platform/api`. Docker Compose verified toolkit (`verbara-verify-image.sh` + `docker-compose.verified.yml` + `verbara-quickstart.sh`). New `.github/workflows/release.yml` (single-pass build via docker/build-push-action@v6 + cosign sign + verify-after-sign).
  - **First signed Verbara.Platform image** at `ghcr.io/verbara/platform/api:v2.1.0` with manifest-list digest `sha256:f82a9041dc7f26018f6b6b11addf3ddbda6a7833827434f6b8d5ca2486349902`. CI workflow run 25636962512 (2m 15s). Cosign signature verified locally + against committed `.github/cosign.pub`.
  - **First authorized digest registered** in `verbara-website/data/authorized-digests.json` (commit `2e41314`). The verbara.io issuer Worker now embeds this digest into every newly-issued `.lic` file's `AuthorizedImageDigests` claim. Pro v2.3.0-pro consumers verify the running image's `IMAGE_DIGEST` env var matches the license claim.

  **Architectural pivot during the rc1-rc4 cycle** — see [Pro ADR-0011 Status update of 2026-05-10](../../../Verbara.Sdk.Pro/docs/decisions/0011-image-digest-binding-in-license-keys.md#status-update). Original ADR proposed two-pass build to bake `/etc/verbara-image-digest` into the image. rc3 smoke test (pull + read file) exposed the chicken-and-egg flaw: an OCI image cannot self-reference its own manifest-list digest because baking the digest changes the contents. rc4 pivoted to operator-side `IMAGE_DIGEST` env var (Helm `api.image.digest` value + docker-compose `environment:` block + dev-mode null-permissive fallback). Pro source code unchanged — `ContainerImageDigest.ReadFromEnvironment()` already had the env-var path as a fallback in v2.3.0-pro from Phase 1.

  **Trigger 5 status: ✅ GREEN.** Trigger 5 conceptually exits when (a) the image-binding machinery exists, (b) a real signed image has been published, and (c) at least one license has been issued that consumes the digest. (a)+(b) above; (c) starts on the next license issuance after the verbara-website Worker redeploys (operator action, ~5 min: `cd verbara-website && npm run deploy`).

  **Trigger dashboard delta (2026-05-10, end of day):**
  ```
  Before this entry: ✅ 6/7 (1, 2, 3, 4, 6, 7) · 🟡 1/7 (5) · ❌ 0/7
  After this entry:  ✅ 7/7 (1, 2, 3, 4, 5, 6, 7) — visibility flip can proceed at maintainer's discretion
  ```

  **Operator action remaining for visibility flip itself** (not part of Trigger 5; this is the actual flip operation gated by all 7 triggers being green):
  1. `npm run deploy` from `verbara-website` to push the updated `authorized-digests.json` live.
  2. `gh api -X PATCH repos/verbara/Verbara.Platform -f visibility=public` (and Web mirror per Web ADR-0007).
  3. Toggle `ghcr.io/verbara/platform/api` package from private to public in repo Settings → Packages.
  4. Announce via verbara.io blog + HN "Show HN" (deferred per visibility plan §4).

- **2026-05-10 19:04 UTC (🎉 visibility flip EXECUTED)**: All seven triggers GREEN; coordinated flip operations executed at maintainer's discretion within ~5 minutes of the Trigger 5 closure entry above.

  **Steps executed**:
  1. ✅ `npm run deploy` from `verbara-website` — Worker version `60b1bb80-697d-489d-aea8-5ba9eda6e222` deployed live with the new `authorized-digests.json` (carries first entry: `Verbara.Platform v2.1.0` digest `sha256:f82a9041...`). Cron `17 3 * * * UTC` re-registered.
  2. ✅ `npx wrangler d1 migrations apply verbara-license-audit --remote` — D1 migration `0002_add_authorized_image_digests.sql` applied (idempotent ALTER TABLE adds `authorized_image_digests TEXT NOT NULL DEFAULT '[]'`).
  3. ✅ `gh api -X PATCH repos/verbara/Verbara.Platform -f visibility=public` — repo flipped, license `Apache-2.0` declared in repo metadata.
  4. ✅ `gh api -X PATCH repos/verbara/Verbara.Platform.Web -f visibility=public` — Web repo flipped (mirror per Web ADR-0007).
  5. ✅ Secret scanning + push protection enabled on both repos (auto-PATCH via `gh api` with `security_and_analysis[secret_scanning][status]=enabled` + `secret_scanning_push_protection][status]=enabled`). Free tier — both features unlock automatically post-flip; PATCH made them active immediately rather than waiting for first sync.
  6. 🟡 **`ghcr.io/verbara/platform/api` package — pending UI flip**. GitHub does NOT expose package visibility via REST API for org-scoped containers (verified 404 on `PATCH /orgs/verbara/packages/container/platform%2Fapi/visibility` — endpoint is UI-only). Operator step: <https://github.com/orgs/verbara/packages/container/platform%2Fapi/settings> → Danger Zone → Change visibility → Public.

  **State at flip time**:
  - `Verbara.Platform` (Apache 2.0) — public, latest tag `v2.1.0`
  - `Verbara.Platform.Web` (Apache 2.0) — public, latest tag `v3.0.1`
  - `Verbara.Sdk` (MIT) — public (was already public pre-flip)
  - `Verbara.Sdk.Pro` (commercial) — **stays private intentionally** (open-core engineering moat per ADR-0016 §"Why Apache 2.0 + commercial Pro")

  **What this activates**: ADR-0016's funnel-driven evaluator-to-Pro-customer rationale (~$1.08M ARR modelling at 1000 GitHub visitors/month → ~3 conversions/month at $30k/customer) is now load-bearing. The "open-core honest" narrative (vs Twilio/Genesys closed competitors) has live source-code evidence to back it up.

  **Audit trail**: `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md` moved to `docs/plans/completed/` with closure status header. Web mirror plan moved likewise. This ADR remains as the canonical decision record.

  **Next-quarter follow-ups** (not blocking; tracked in [`project_2026_05_10_visibility_flip_executed.md`](memory file in user-level auto-memory)): announcement (HN/Reddit/Twitter), real-customer e2e validation, Web image-binding (currently `TODO(web-image-binding)` marker in `docker/verbara-quickstart.sh`), backfill `authorized-digests.json` automation via cross-repo Action.

- **2026-05-09 (later — Trigger 3 ✅ GREEN; v2.0.1 ships P0+P1 closures)**: All 6 P0+P1 findings closed. v2.0.1 commits on `main` (the release branches were created with the legacy `release/v1.13.x-*` label before the v2.0.x post-rebrand naming was settled — branches kept as historical labels; the actual release tag is v2.0.1):
  - Phase 0 + Phase 1 (the 2 P0s): `4718a870` (`CrossTenantHeaderAttackFixture`), `3a90300b` (`MT-001` — `TenantBoundaryValidationMiddleware`), `23409c55` (`ADMIN-001` — `IDataProtectionProvider` wrap + `OidcClientSecretEncryptionMigrator` + redacted response DTO + 4 Api + 4 Storage Testcontainers tests).
  - Phase 2 (the 4 P1s): `baa7aaef` (`MFA-001` — async hierarchy resolver + `MfaPrivilegeEscalationAttempted` audit event), `2b83604a` (`BILL-001` + `BILL-002` — 8 audit emissions on billing handlers + `PayInvoice` `?tenantId=` cross-check + `EntityId.IsValid`), `c35a0d17` (`ADMIN-002` — scope-aware `PlatformAdminAuthorizationHandler` + new ADR-0019 documenting back-compat through v3.0.0).

  Verification: full `dotnet test Verbara.Platform.slnx` PASSES across 30 test assemblies; `Verbara.Platform.Api.Tests` 932/932 (897 baseline + 35 new — 14 P0 + 21 P1); `Verbara.Platform.Storage.Postgres.Tests` 34/34. Zero source-code warnings under `TreatWarningsAsErrors`. Threat model gets append-only Status update of even date covering §6.2 (Tampering — fixed), §6.3 (Repudiation — comprehensive billing audit), §6.4 (Information-Disclosure — OIDC plaintext closed), §6.6 (Elevation-of-privilege — two new ✅ Verified-fixed rows for MFA tenant-scoping and management-key permissions).

  **Trigger 3 status: ✅ GREEN.** New ADR-0019 (`docs/decisions/0019-scope-aware-management-api-keys.md`) Accepted, documenting management-key permission model change with v2.0.x → v2.1.0 → v3.0.0 deprecation timeline (removal of the wildcard is a breaking SemVer change and therefore lands at the next major).

  **Trigger dashboard delta (2026-05-09, end of session):**
  ```
  ✅ GREEN:    6/7  (Triggers 1, 2, 3, 4, 6, 7)
  🟡 PARTIAL:  1/7  (Trigger 5 — Pro v2.3.x execution: cosign keypair + image-digest binding)
  ❌ BLOCKED:  0/7
  ```
  Visibility flip is now gated **only** by Trigger 5 execution (~5 days work, planned in `Verbara.Sdk.Pro/docs/plans/active/2026-05-09-pro-v23x-image-binding-execution.md`).

- **2026-07-05 (confirmed-public, doc-drift remediation):** Re-verified live against GitHub (`gh repo view --json visibility` → `PUBLIC`) during a cross-repo docs-drift audit. `README.md`'s visibility banner had drifted back to citing the pre-flip 2026-05-08 "currently private" status despite the flip executed 2026-05-10 (recorded above) — corrected to reflect the confirmed-public state. No change to the trigger checklist or decision content; this is a status-freshness note only.

## References

- ADR-0016 (this repo) — license decision (Apache 2.0) and rebrand to Verbara
- ADR-0017 (this repo) — rebrand execution (versioning)
- Web ADR-0006 — license + 5-tier commercial strategy
- Web ADR-0007 (when shipped) — Web-side mirror of this decision
- SDK ADR-0027 — stewardship pledge
- SDK auto-memory `project_2026_05_08_licensing_audit.md` — the audit that surfaced this gap
- Active plan `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md`
