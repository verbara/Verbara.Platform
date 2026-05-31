# Manuales staleness audit vs Platform v2.6.0

**Date:** 2026-05-30 · **Method:** 13 parallel auditors (one per manual + one for `auto/` living-docs) cross-referenced against the v2.6.0 change delta, with adversarial verification of every REDO verdict against shipped code. **Trigger:** v2.6.0 release (ADR-0026 A+B membership executive routing + ADR-0028 mandatory-Customer setup).

## Change delta audited

- **D1 — ADR-0028 mandatory-Customer setup (behavior):** `POST /api/v1/setup` now provisions Platform root + a **first Customer tenant + both admins** in one call (was Platform-only). `SetupRequest` hard-requires `CustomerTenantId/CustomerName/CustomerAdminEmail/CustomerAdminPassword` (HTTP 400 otherwise). Day-to-day config (queues/agents/channels/trunks) must happen **inside the Customer tenant** — operational calls against the `platform` root return **HTTP 409** (`RequireOperationalTenant`, ADR-0027). Wizard (`setup-page.tsx`, v3.2.0-web) adds a Customer fieldset + raised password min to 12.
- **D2 — ADR-0026 A+B membership executive routing (behavior):** `queue_memberships` is now the executive source of truth for routing across **all** channels (voice + digital). `MembershipAwareRoutingEligibilityService` intersects presence ∩ memberships and gates on `allowed_channels`. An agent receives a channel's conversations **only** if a member of a queue whose `allowed_channels` is NULL or contains that channel. Web editor at `/admin/agents/{id}/queues`.
- **D3 — image tags:** all images `v2.6.0`, web `v3.2.0-web` (bulk-bumped 2026-05-30).
- **D4 — cosign:** v3.0.6, verify vs `https://verbara.io/keys/cosign.pub` with `--insecure-ignore-tlog`.

## Verdicts (13 units)

| Manual | Verdict | Eff | Dominant drift | Notes |
|---|---|---|---|---|
| **06-canal-voz-sip.md** | 🔴 REDO | M | D2 + D1 | Never refreshed since 2026-05-17. Voice membership gate missing + 4 curls on `platform` + pre-existing path bugs. |
| **04-canal-webchat.md** | 🔴 REDO | M | D1 | Only *partially* refreshed by ADR-0026 commit — widget `data-tenant-id="platform"` (chats land nowhere) + curls on `platform` + pre-existing endpoint/verb bugs. |
| **05-canal-email.md** | 🔴 REDO | M | D2 | Membership-executive section entirely MISSING (sibling 04 has §3.1; 05 has nothing). |
| **checklist-validacion-cliente.md** | 🔴 REDO | S | D1 | §4 describes single admin + manual tenant creation; needs two-tenant/two-admin rewrite + membership validation step. |
| **auto/ living-docs (v2.5.4+v2.5.5)** | 🔴 REDO | M | D1 | Day-1 setup journey predates ADR-0028 (single tenant/admin diagram + 4-field form + screenshots). **→ Living-docs track.** |
| **02-arranque-stack.md** | 🟡 REFRESH | S | D3 | Phantom `git checkout v2.5.5` (never released) + closing teaser singular-tenant. |
| **07-validacion-e2e.md** | 🟡 REFRESH | S | D1 | Setup outcome omits 2nd tenant/admin; add membership verification note. |
| **00-vision-general.md** | 🟢 OK | — | — | One optional TOC caption ("admin + tenant" singular). Cosmetic. |
| **01-instalacion-docker.md** | ✅ OK | — | — | Host-prep only; no v2.6.0 surface. |
| **03-setup-inicial.md** | ✅ OK | — | — | ADR-0028 refresh (602bd733) was **complete + correct**. Reference pattern for Customer-tenant curls. |
| **08-troubleshooting-sip.md** | ✅ OK | — | — | Version-independent SIP reference. |
| **99-troubleshooting.md** | ✅ OK | — | — | `v2.5.4` is an intentional upgrade example. |
| **capacity-reference.md** | ✅ OK | — | — | Tier numbers stable (post-D-LK refresh is a separate concern). |

## Per-manual fix specs (REDO + REFRESH)

### 06-canal-voz-sip.md — REDO (M)
- **D2 (material):** §6 only provisions a PJSIP endpoint + logs the agent in; never creates/verifies a `queue_memberships` row with `voice` in `allowed_channels`. Under v2.6.0 a PJSIP endpoint alone is **insufficient** — the agent silently receives no calls without voice membership. §5 routing table frames assignment as queue-strategy + skill-match only, with no membership-gate prerequisite. **Fix:** add a voice-membership step to §6 + a §7 check; preface §5 stating the strategy selects only among voice-enabled members; reference `/admin/agents/{id}/queues` + "Voice → Asterisk" badge.
- **D1 (material):** 4 curls hardcode `X-Tenant-Id: platform` (L103 trunks, L183 inbound-routes, L260 queues, L287 provision-webrtc) + §6.3 login Tenant ID = `platform`. **Fix:** re-point to the Customer tenant; mirror **03** (cleaner reference than 04).
- **Pre-existing bug (found in verify, fix in same pass):** route paths `/api/v1/dialer/trunks` → real is `/admin/trunks` (TrunkEndpoints); inbound-routes is not under `/dialer/`.

### 04-canal-webchat.md — REDO (M)
- **D1 (material):** every API example targets `X-Tenant-Id: platform` (§1/§3/§6/§7) → now HTTP 409 (operational-gated). Widget `data-tenant-id="platform"` + iframe `?tenant=platform` (§2/§5) → chat sessions land nowhere. **Fix:** re-point to Customer slug + add the ADR-0027 advisory (as 03 does). Note: there is no "single-tenant" case anymore.
- **Pre-existing bugs (found in verify):** §3 `POST /api/v1/admin/routing/inbound` **does not exist** in the codebase (fictional). §1/§6 use **PATCH** but the real endpoint is **PUT** (`MapPut`, no `MapPatch`). §7 analytics per-channel-summary path doesn't map as written. The rewrite must fix endpoints/verbs, not just swap the tenant header. 04's own operational curls (L50/97/211/221) were left on `platform` by the partial ADR-0026 refresh.

### 05-canal-email.md — REDO (M)
- **D2 (material):** no mention of `queue_memberships`/`allowed_channels` anywhere; §2.4 only sets a queue, §7 troubleshooting walks IMAP/pipeline/rules but never the membership gate. `ChannelType.Email = 4` → gate string `"Email"`. **Fix:** add a membership-executive section parallel to WebChat §3.1 (agent needs a non-excluded membership whose `allowed_channels` is NULL or contains `Email`; reference `/admin/agents/{id}/queues` + "Digital only" badge) and extend §7 with a membership-gate check.
- **D1:** `X-Tenant-Id` examples — change only as part of a suite-wide tenant pass (sibling 04 also still uses `platform`, so this is currently a suite convention, not an isolated defect).

### checklist-validacion-cliente.md — REDO (S)
- **D1 (material):** §4 L82 "Platform admin creado vía POST /api/v1/setup" (single admin), L86 frames first tenant as a manual post-setup step. **Fix:** rewrite §4 — setup creates Platform + first Customer + two admins in one call; add a credential-capture line for the **second** (customer) admin; reframe L86 (the Customer tenant already exists — capture its ID); note operational config lives in the Customer tenant.
- **D2 (minor):** add a membership validation step (agent is a member of the target queue + `allowed_channels` includes the channel under test) before the WebChat/voice round-trip checks.

### auto/ living-docs — REDO (M) → **Living-docs E2E track**
- **D1 (material, ×4):** `01-day1-setup-and-webchat.md` (v2.5.4) diagram shows one tenant/one admin; setup-completion prose says "platform admin" only; Paso 2 form has 4 fields (real wizard now has a Customer fieldset + min-12 password); verification curls + widget use `tenant=platform`. Also `02-agent-channel-routing.md` (v2.5.5) L52/L155 use `tenant=platform`. **Fix:** regenerate the Day-1 journey against v2.6.0 (diagram + narrative + screenshots + Customer-tenant pointing) — this is living-docs regeneration, not a hand-edit. D2 membership content in `02` is accurate; only its tenant pointing needs the same fix.

### 02-arranque-stack.md — REFRESH (S)
- **D3 (minor):** L303-304 `git checkout v2.5.5` (never released) → generic placeholder. **D1 (minor):** closing teaser L321 singular "admin + tenant" → two-tenant wording.

### 07-validacion-e2e.md — REFRESH (S)
- **D1 (minor):** setup outcome now always two tenants + two admins; clarify config lives in the Customer tenant. **D2 (minor):** add membership verification note.

## Cross-cutting conclusions

1. **The dominant drift is D1 tenant-pointing.** ADR-0028 made the `platform` root non-operational (409). Every manual showing `X-Tenant-Id: platform` for channel/queue/agent/trunk config is now a **customer-facing correctness bug** — the first customer following 04/06 hits HTTP 409/404. This is independent of (and more urgent than) the living-docs migration.
2. **04 was only partially refreshed** — the ADR-0026 commit migrated its membership prose (§3.1) but left every operational curl + the widget on `platform`. "Recently touched" ≠ "v2.6.0-correct."
3. **03 is the gold reference** for the Customer-tenant curl pattern (explicit ADR-0027 409 warning + `mi-empresa` slug). Mirror it.
4. **The audit doubles as a pre-existing-bug finder:** 04 (fictional `/admin/routing/inbound`, PATCH-vs-PUT, bad analytics path) and 06 (`/dialer/trunks` vs `/admin/trunks`) carry route/verb bugs unrelated to v2.6.0. Fix in the same pass.
5. **Living-docs scope is narrower than the manual suite:** Phase 1 covers smb-owner Day-1/Day-2 journeys (setup + webchat + agent-channel-routing). The channel manuales (05 email, 06 voice/SIP) and the checklist are **not** in living-docs Phase 1 → they need hand-fixing regardless.

## Recommended sequencing

- **P1 (fix now, hand-written, customer-facing correctness):** 06 → 04 → 05 → checklist. Includes the pre-existing endpoint/verb bug fixes.
- **P3 (batch with P1, minor):** 02, 07 (+ optional 00 caption).
- **P2 (Living-docs E2E track):** regenerate the `auto/` Day-1 setup journey against v2.6.0; fix `02-agent-channel-routing` tenant pointing.
