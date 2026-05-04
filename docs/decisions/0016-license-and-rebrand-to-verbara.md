# ADR-0016: License (Apache 2.0) + Rebrand to Verbara

- **Status:** Accepted
- **Date:** 2026-05-03
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:** [Asterisk.Platform.Web ADR-0006](https://github.com/Harol-Reina/Asterisk.Platform.Web/blob/main/docs/decisions/0006-license-and-commercial-tier-strategy.md) — full multi-pass analysis lives there; this ADR mirrors the decision in Platform.

## Context

Until 2026-05-03 this repository had no `LICENSE` file. The README mentioned "Open-core. The base SDK is MIT. Pro features (commercial) require a license key validated by `Pro.Licensing` (ECDSA)" — a vague description that did not specify which license governs **this** repository (the Platform backend, distinct from the SDK and Pro overlays it consumes).

Two decisions were coupled and made simultaneously:

1. **Which license governs Platform source code?**
2. **What name does the project ship under publicly?**

The license decision required a deep multi-pass analysis (see Web ADR-0006 for the full rationale). The brand decision was triggered by trademark research:

- "Asterisk" is a registered trademark of **Sangoma Technologies / Digium** (Sangoma acquired Digium in 2018).
- Precedent: the **FreePBX** project was forced to rename in v2.0 because of this trademark.
- Continuing to use "Asterisk" in product / repo / package names exposes the project to cease-and-desist and forced rename **post-launch**, after brand equity has accrued — the worst possible time.

The license + brand decisions interact: the LICENSE file's copyright line and contact email must be coherent with the project's public identity. So both were resolved together.

### Why this matters for Platform specifically

Platform is a **monetizable backend** (per PSD v2): full contact-center application, billing engine, multi-tenant infra, audit log, IP allowlist, OIDC + MFA, 11 channels, runtime-mandatory `LicenseGateMiddleware` (ECDSA-validated Pro keys). The license choice has direct revenue implications.

The initial proposal was BUSL-1.1 (Business Source License) for SaaS-competitor protection. After financial modeling, it was reverted to Apache 2.0. Reasons:

1. **BSL has zero court precedent globally** as of May 2026 — protection is theoretical.
2. **Apache yields ~2× ARR vs BSL** based on legal-review-friction funnel modeling (95% vs 20% pass-through rate at enterprise legal review). Open Core Ventures (2024) reports AGPL is a "non-starter for most companies"; BSL is worse because it is non-OSI-approved (GitHub badge shows "Other") and unfamiliar to corporate legal teams.
3. **Pro license keys (ECDSA-validated by `LicenseGateMiddleware`) are the only enforceable runtime moat.** Source-license restrictions (BSL, ELv2, AGPL) require litigation — and the founder cannot afford $150k-500k in legal fees pre-revenue.
4. **CCaaS market reality** (NICE 22.3% share, Genesys $2.4B ARR +33% YoY) shows no successful CCaaS competitor uses BSL/source-available licenses. The market is bipolar: AGPL community plays (Vicidial, Erxes — infrautilized in enterprise) vs fully closed proprietary (Twilio, Genesys, Five9). Apache + commercial Pro engine + hosted SaaS is an unoccupied sweet spot.

## Decision

### Rebrand: Asterisk → Verbara

The product family is rebranded to **Verbara** (verbara.io). Effective immediately for new public materials (LICENSE, NOTICE, READMEs, CONTRIBUTING). Repository names (`Asterisk.Sdk`, `Asterisk.Sdk.Pro`, `Asterisk.Platform`, `Asterisk.Platform.Web`) and .NET namespaces (`Asterisk.Platform.X`) remain temporarily as a transitional state. They will be renamed to `verbara-*` and `Verbara.Platform.X` respectively in a coordinated technical rebrand track post-Track 1A.

**Why Verbara:** From Latin *verbum* ("word") + suffix *-ara*. Strong communication semantic, Spanish/Portuguese-friendly (matches LATAM market focus), invented word (defensible legally as a trademark). GitHub username `verbara` available; `verbara.io` / `.dev` / `.app` available; `.com` parked at squatter (acquirable later); USPTO basic search clean; no major brand conflict in CCaaS or telecom.

### License: Apache 2.0

This repository (`Asterisk.Platform`, future `verbara-platform`) is licensed under the **[Apache License 2.0](../../LICENSE)**.

| Repo (current) | Future name | License | Already published? |
|---|---|---|---|
| `Asterisk.Sdk` | `verbara-sdk` | **MIT** (no change) | Yes |
| `Asterisk.Sdk.Pro` | `verbara-sdk-pro` | **Commercial proprietary** (no change) | Yes |
| `Asterisk.Platform` (this repo) | `verbara-platform` | **Apache License 2.0** | NEW (LICENSE / NOTICE / CONTRIBUTING shipped 2026-05-03) |
| `Asterisk.Platform.Web` | `verbara-web` | **Apache License 2.0** | NEW (shipped 2026-05-03) |

Copyright line: `Copyright 2026-present Harol A. Reina H. and Verbara Contributors`. Year format `YYYY-present` per industry convention for living repositories.

### Contributor license model

We use **DCO** (Developer Certificate of Origin) via `git commit -s` instead of a CLA. Lighter weight, used by Linux Kernel and Docker. Apache 2.0's outbound license is sufficient for the inbound flow at this time. CLA can be introduced later if a future re-licensing (e.g., dual Apache + AGPL post-revenue) requires it.

### Identity infrastructure

- **Domain:** `verbara.io` (primary, registered)
- **Email aliases (Cloudflare Email Routing, free):**
  - `legal@verbara.io` — copyright disputes, DMCA, legal matters
  - `security@verbara.io` — vulnerability disclosure (RFC 9116)
  - `licensing@verbara.io` — commercial license inquiries, partnerships
  - `hello@verbara.io` — general contact
- **GitHub organization:** `github.com/verbara`
- **Brand tagline:** *"Open-core honest contact-center platform — auditable engine, commercial overlays."*

## Consequences

**Positive:**
- Maximizes evaluator-to-Pro-customer conversion funnel — Apache 2.0 has near-zero legal-review friction, OSI-approved, every enterprise compliance team recognizes it.
- Zero legal infrastructure overhead at day 1 — no CLA infrastructure, no "Additional Use Grant" wording, no Change Date strategy. Estimated 6-8 weeks of engineering time saved vs BUSL setup.
- Apache 2.0's explicit patent grant protects contributors and downstream users — material for a substantial codebase (19,197 LOC, 31 packages, 11 channels).
- Trademark exposure eliminated by rebrand to Verbara before public launch (no "FreePBX moment" forcing a rename later under brand pressure).
- The 5-tier commercial structure (per Web ADR-0006) leverages the existing `LicenseGateMiddleware`, `PlanFeature` enum, `Pro.MultiTenant`, `Pro.Licensing` infrastructure. No new technical investment to start tiering customers.
- Reversible at the top: if revenue grows and competitive pressure justifies, can move to AGPL or BSL via triple-licensing model — at which point the company has revenue, brand, and customer relationships to defend the change.

**Negative:**
- Forfeits the (theoretical) "first BSL CCaaS" marketing angle. Analysis concluded this is not material for actual buyers, who evaluate by features/price/SLA/AI/compliance, not by license model.
- Apache 2.0 in theory allows a well-funded competitor to fork Platform and reverse-engineer Pro to offer a competing managed service. In practice this requires 12-18 months of engineering, brand-building, and certification work. The Pro ECDSA gate at runtime remains the binary-level moat.
- The rebrand introduces a transitional period where repository names, package names, namespaces, and Docker image names still say "Asterisk" while public branding says "Verbara". Documented as such in READMEs; coordinated rebrand track will resolve over ~3-5 calendar weeks.

**Trade-off:**
- Trades theoretical legal protection (BSL non-compete) for measurable adoption velocity (Apache zero-friction). Given pre-revenue stage and single-founder constraints, this favors revenue acceleration over defense of an asset that does not yet generate revenue. Acceptable.

## Alternatives considered

- **MIT.** Rejected because Apache 2.0 adds explicit patent grant + NOTICE attribution mechanism without meaningful adoption cost. For a substantial backend (19k LOC, 11 channels, billing engine, audit log), patent protection is materially valuable.
- **AGPL-3.0.** Rejected because Open Core Ventures (2024) and multiple sources confirm AGPL is rejected at corporate policy level by 40-60% of enterprise buyers. Would protect against SaaS strip-mining but at the cost of ~50% of the addressable market in enterprise CCaaS.
- **BUSL-1.1 + Commercial dual + CLA.** Initially recommended in V1 of the planning analysis. Rejected after funnel modeling and risk analysis revealed: (a) zero court precedent, (b) 2-4 weeks added to enterprise legal review on every evaluation, (c) ~50% reduction in legal-review pass-through rate, (d) 6-8 weeks of legal infrastructure setup pre-launch. Runtime ECDSA gate (already implemented in Pro) provides equivalent protection without these costs.
- **Elastic License v2 (ELv2).** Rejected for same reasons as BSL plus: ELv2 carries negative brand association from re-licensing controversies (Elastic 2021, Redis 2024, MinIO 2024). Starting day 1 with ELv2 inherits the negative brand without earning it.
- **Closed proprietary** (matching `Asterisk.Sdk.Pro`). Rejected. Eliminates evaluation funnel entirely, requires sales motion before any product touch, removes the "open-core honest" differentiator. Closed source is appropriate where the IP itself is the moat (Sdk.Pro algorithms, license keys); for the application layer that buyers want to inspect for compliance, openness is a feature.
- **Keeping the "Asterisk" brand.** Rejected after trademark verification. "Asterisk" is registered to Sangoma/Digium with active enforcement history (FreePBX rename precedent). Continuing creates 3-12 month timeline to forced rename under brand pressure — worst possible scenario. Better to rebrand pre-launch when blast radius is minimal.
