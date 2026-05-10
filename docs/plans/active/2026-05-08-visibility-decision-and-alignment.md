# Platform Visibility Decision (Decision 3) + Doc Alignment

**Created:** 2026-05-08
**Status:** Active (planning, not yet executed)
**Repo:** `/media/Data/Source/Verbara/Verbara.Platform/`
**Origin:** Cross-repo licensing & visibility audit run 2026-05-08 in `Verbara.Sdk` session. Full findings in SDK auto-memory `project_2026_05_08_licensing_audit.md`.

## Context

`Verbara.Platform` declares **Apache 2.0** license in `LICENSE`, README, and ADR-0016 (Accepted 2026-05-03). The README opens with *"Backend for the Verbara open-core contact-center platform"*, lists Apache 2.0 in the License section, and shows the 4-row stack table. ADR-0016's financial model is built on the premise of public visibility:

> *"Apache yields ~2× ARR vs BSL based on legal-review-friction funnel modeling (95% vs 20% pass-through rate at enterprise legal review). Funnel: 1000 GitHub visitors/month → ~3 conversions/month at $30k/customer = $1.08M ARR."*

But the GitHub repo is **private**. Apache 2.0 does not legally require public source — it only governs distribution — but the strategic rationale documented in ADR-0016 cannot operate while the repo is unobservable.

This plan does **not** revisit the Apache 2.0 license decision (correct per ADR-0016) and does **not** flip visibility today (git history not audited, security review not done). It documents and operationalizes **Decision 3**: keep private now, with explicit triggers to go public.

## Goal

By end of this plan: Platform has (a) an Accepted ADR formalizing Decision 3 with concrete go-public triggers, (b) README updated to clarify current visibility status, (c) the trigger checklist captured as actionable items in this plan, (d) a `verbara.io` brand-domain dependency surfaced.

What is NOT in scope: making the repo public, building the customer portal, writing the EULA (lives in Pro plan).

## Non-goals

- **Flipping visibility to public today** — premature without git audit + security review
- **Writing a new ADR that supersedes the Apache 2.0 decision** — Apache 2.0 is correct; only the *visibility* execution path needs documentation
- **Customer portal / billing UI in Platform** — these are separate concerns living elsewhere
- **Pro EULA work** — owned by Pro plan `2026-05-08-pro-licensing-eula-overhaul.md`

---

## Phase 0 — ADR foundation (Wk 1, ~3h)

### 0.1 — Write ADR-0018: Visibility Decision (Decision 3)

(Note: ADR-0017 already exists, documenting Verbara rebrand execution. Next sequential is 0018.)

- [x] New ADR `docs/decisions/0018-visibility-decision-3-private-now-public-on-trigger.md` — **DONE 2026-05-08**
- [x] Status: Accepted — **DONE**
- [ ] Sections:
  - **Context** — ADR-0016 chose Apache 2.0 with public-funnel rationale; repo currently private; this ADR resolves the gap
  - **Decision** — Platform stays private until ALL trigger conditions met; conditions enumerated below; once all green, repo flips to public in a single coordinated change
  - **Trigger checklist (must all be ✅ before flipping):**
    1. `gitleaks detect` clean across full history (no leaked secrets, JWTs, AWS keys, .env content)
    2. Operational Foundation closed in Web (v1.14.x track) — implies Platform has CI/CD, error tracking, security headers
    3. Internal security review of sensitive endpoints (`/api/v1/billing/*`, multi-tenant boundary tests, admin endpoints)
    4. Public threat model documented (`docs/security/threat-model.md`) — what's exposed by going public, what's still ECDSA-protected
    5. `LicenseGuard` not bypassable by trivial reflection/IL editing (Pro plan Phase 0 exit)
    6. `verbara.io` domain + brand setup complete (apex site, doc subdomain, contact emails active)
    7. **Trigger event**: first signed Tier 1+ customer demo (validates pricing + tier model)
  - **Consequences (positive)** — honors ADR-0016 economics; gains free secret scanning + push protection (private repo blocks them today); aligns Tier 0 Community narrative with reality
  - **Consequences (negative)** — delays funnel-launch by ~2-3 sprints; risk that competitors fork once public (mitigated by `LicenseGuard` ECDSA + Pro 18-month engineering moat per ADR-0016)
  - **Alternatives considered** — Decision 1 (publish today): rejected, secret/security risk; Decision 2 (private forever, supersede ADR-0016): rejected, wasted strategic thinking + breaks "open-core honest" narrative
  - **References** — ADR-0016, SDK ADR-0027 (stewardship), this plan, audit memory

### 0.2 — README addendum (transparency)

- [x] Add a short visibility-status note near the top of README (between the rebrand notice and "Quick start") — **DONE 2026-05-08**:

  ```
  > **Visibility status:** This repository is currently private. The Apache 2.0
  > license has been chosen (see [ADR-0016](docs/decisions/0016-license-and-rebrand-to-verbara.md))
  > with a planned transition to public when all triggers in
  > [ADR-0018](docs/decisions/0018-visibility-decision-3-private-now-public-on-trigger.md)
  > are met. Tier 0 (Community) self-host becomes available at that time.
  ```

- [ ] Verify no other README sections imply present-tense public availability that isn't true ("Get started in 30 minutes" stays accurate; "fork on GitHub" would not — search and adjust if found) — **TODO** (low priority)

**Phase 0 exit:** ADR-0018 Accepted; README clarifies current status without contradicting ADR-0016.

**Phase 0 status 2026-05-08:** 0.1 ✅ DONE / 0.2 ✅ DONE (full README sweep deferred).

---

## Phase 1 — Trigger checklist execution (parallel, sprint-scoped)

These are the work items that, when all complete, allow the visibility flip. Most are owned by other tracks; this plan tracks them as gates. Triggers are enumerated in ADR-0018 (the canonical source).

### Trigger 1 — gitleaks audit (Wk 1, ~1h)

- [x] Run `gitleaks detect --source . --no-banner` — **DONE 2026-05-08**: 6 findings, all reviewed
- [x] For each true positive: rotate the secret, decide history-rewrite vs rotate-and-document — **DONE 2026-05-08**: zero true positives. All 6 findings are demonstrable test fixtures or self-signed demo certs. No rotation needed, no history rewrite needed.
- [x] Write `docs/research/2026-05-08-gitleaks-audit.md` summarizing what was found and how each was handled — **DONE 2026-05-08**
- [x] Re-run until exit code 0 — **N/A** (current 6-finding baseline accepted as documented; no leaks of actual secrets)

**Trigger 1 status: ✅ GREEN.**

### Trigger 2 — Operational Foundation (owner: Web track v1.14.x, scope-shared)

- Tracked in `Verbara.Platform.Web/docs/plans/active/2026-05-03-v1.14.x-operational-foundation-roadmap.md`
- Platform-side effects: needs to expose `/health/ready`, `/metrics`, error-tracking integration (already done per existing roadmap)
- [ ] Cross-check that ALL operational-foundation gates have green CI when Web track closes

### Trigger 3 — Sensitive-endpoint security review (Wk 2-3, ~1 day focused)

- [x] Catalog endpoints touching `/api/v1/billing/*`, tenant-boundary, admin operations — **DONE 2026-05-09** (60 endpoints catalogued in pre-public review doc)
- [x] Threat-model each: authn → authz → tenant scoping → input validation → output filtering → audit log — **DONE 2026-05-09** (10 findings: 2 P0, 4 P1, 4 P2)
- [ ] Add tests for any boundary not yet covered (multi-tenant cross-access, privilege escalation, MFA bypass) — **DEFERRED** to v1.13.x patch train fix tickets (test added per fix)
- [x] Document the review in `docs/security/2026-05-09-pre-public-security-review.md` — **DONE 2026-05-09**

**Trigger 3 status: ❌ BLOCKED.** Catalog + audit done, but 2 P0 findings (cross-tenant via `X-Tenant-Id` header on `/admin/*`; OIDC client secret persisted plaintext) and 4 P1 findings must be fixed in code before flip. Detailed findings + remediation plan in `docs/security/2026-05-09-pre-public-security-review.md`. Threat model updated with append-only Status entry referencing this audit. Path forward: v1.13.x patch train — see new remediation plan (TBD).

### Trigger 4 — Public threat model (Wk 3, ~4h)

- [x] `docs/security/threat-model.md` — what assets exist, what is exposed by being public, what is still protected, who the threat actors are, mitigations per threat — **DONE 2026-05-09**
- [x] Mirror Pro's threat model (LicenseGuard, ECDSA validator) — explicit "Pro is closed; Platform/Web open by design but the binary moat is in Pro" — **DONE 2026-05-09** (covered in §4 "What remains protected" and §8 residual-risk row on `LicenseGateMiddleware` bypass)

**Trigger 4 status: ✅ GREEN.**

### Trigger 5 — LicenseGuard tamper resistance (owner: Pro plan, gate here)

- [ ] Cross-check that Pro plan `2026-05-08-pro-licensing-eula-overhaul.md` Phase 0 has shipped tamper-resistance baseline before flip

### Trigger 6 — verbara.io brand setup (owner: separate track)

- [ ] verbara.io apex (landing)
- [ ] docs.verbara.io (or docs.verbara.io/platform/, depending on subpath strategy)
- [ ] Email aliases active (legal/security/licensing/hello@verbara.io)
- [ ] Tier 0.5 Developer auto-issue endpoint (owner: Pro plan Phase 3)

### Trigger 7 — First signed Tier 1+ customer demo (business event)

- [ ] Track in CRM or `docs/operations/customer-pipeline.md` (private)
- [ ] Demo signed = pricing validated + tier model real

**Trigger 7 status: ✅ GREEN (Option B formalised 2026-05-09).** Maintainer chose to soften this trigger from "first paying customer" to "Tier 0.5 e2e portal validation by external evaluator". Smoke test executed 2026-05-09: license `verbara-developer-399812ee-bf2c-4246-824e-6ed92c4783c1.lic` issued by verbara.io Worker, downloaded by maintainer, validated through full `LicenseReader` → `LicenseValidator.Validate` → `LicenseTrustAnchor.OfficialPublicKey` path against Pro v2.2.0-pro. Result: `LicenseValidationResult.Valid` (Tier=Developer, Features=All=0x1FF, MaxAgents=5, MaxNodes=1, expires 2026-06-08). Formalised in ADR-0018 Status update (2026-05-09) and mirrored in Web ADR-0007 Status update.

---

## Phase 2 — The flip (Wk N, single day when triggers green)

### 2.1 — Pre-flip dry run

- [ ] Verify ALL Phase-1 triggers complete with their evidence
- [ ] Final `gitleaks detect` re-run on current HEAD
- [ ] Final `git log --all --diff-filter=D -- .env` (and similar) — no removed-but-still-in-history secrets
- [ ] Update `docs/research/` audit doc with final exit certificate

### 2.2 — Flip operation

- [ ] `gh api -X PATCH repos/verbara/Verbara.Platform -f visibility=public`
- [ ] Verify GitHub Pages, branch protections, and required reviews are configured
- [ ] Re-enable secret scanning + push protection (now free for public repo)
- [ ] Announce on `verbara.io` blog + Hacker News "Show HN"

### 2.3 — Post-flip ADR update

- [ ] Add a "Status update" section to ADR-0018 noting flip date + triggers verified
- [ ] DO NOT modify the original ADR text (append-only)

**Phase 2 exit:** Platform is public, Apache 2.0, with ADR trail showing the disciplined transition.

---

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| One trigger drags indefinitely (e.g. trademark hung) | Document blocker, decide whether to remove from gate or keep waiting; do NOT flip with gates open |
| Gitleaks finds something with no clean rotation path | History rewrite via `git filter-repo`; document the choice; communicate to any Pro customers if their license keys touched |
| First customer comes before triggers green | Tempting to flip early; resist — sign the deal under private and trigger when the ops debt is paid |
| ADR-0016 is reinterpreted by a future reader as obsolete | ADR-0018 explicitly says it does NOT supersede 0016; the license decision stands, only the execution path is documented |

## Dependencies

- **Pro plan** `2026-05-08-pro-licensing-eula-overhaul.md` — owns LicenseGuard tamper-resistance + Tier 0.5 portal (Triggers 5 + 6)
- **Web plan** `2026-05-08-visibility-decision-and-portal.md` — parallel mirror of this plan for Web; flips together
- **Web v1.14.x Operational Foundation** — Trigger 2 source

## Cross-references

- Audit findings: SDK auto-memory `project_2026_05_08_licensing_audit.md`
- Strategy default: SDK auto-memory `feedback_licensing_strategy.md`
- License decision: ADR-0016 (this repo, Accepted 2026-05-03) — NOT superseded by this plan
- Web mirror ADR: ADR-0006 (Web repo)
- Stewardship pledge: SDK ADR-0027
