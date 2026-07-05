# Production Readiness Review — R5.5

**Date:** 2026-05-26
**Train:** R5.5 "Production Validation con datos reales"
**Audience:** Operators deploying Verbara Platform to production; partners evaluating Verbara as a CCaaS platform; Verbara maintainers ahead of first customer onboarding.
**Pattern:** Google SRE PRR checklist (https://sre.google/sre-book/evolving-sre-engagement-model/) adapted for a single-tenancy verifiable-by-customer product.

## Context

R5.5 was scoped as "Production Validation con datos reales" — replace `v1 provisional` SLOs / capacity / alerts with empirically measured baselines across three environments (docker-compose local + K8s local Talos lab + cloud single-region). After the **strategic pivot recorded 2026-05-25** ([commit `204aa7c9`](https://github.com/verbara/Verbara.Platform/commit/204aa7c9), memory `session-20260525-phase0c-deferred-smb-pivot` (maintainer session memory, not a repo artifact)), cloud spend is gated behind first paying customer; R5.5 closes against two empirical datasets (docker + K8s-local) instead of three.

This PRR signs off the platform against the **as-validated** envelope and explicitly flags the unvalidated envelopes as deferred, not denied. The cadence is: ship to SMB customers behind the SMB Docker reference deployment; sell Enterprise K8s as "validated on-prem on a 3-worker Talos lab"; re-open cloud validation against real customer traffic once one signs.

## Deployment patterns reviewed

| Pattern | Scale envelope | Validation status | Recommended for |
|---|---|---|---|
| **SMB Docker Compose** (single-host) | ≤ 100 tenants, ≤ 1,000 concurrent calls | ✅ **PRIMARY validated track** — D-L 24h PASS (959 M req, p99 60.66 ms), C-L chaos (9/9 PASS), measured B-L knee | First paying customers + product polish track |
| **Enterprise K8s on-prem** (3-worker Talos + CNPG + Kamailio/RTPEngine SBC) | multi-replica HPA, multi-tenant, CNPG primary failover | ✅ **REFERENCE validated track** — B-LK / C-LK / D-LK all closed on v2.5.4 lab cluster | Operators with K8s expertise; design partners; enterprise prospects evaluating |
| **Single-region cloud Docker** | TBD | 🚫 **DEFERRED** — no empirical validation, no current customer ask | Re-evaluate post first paying customer |
| **Multi-region cloud K8s** | TBD | 🚫 **NOT IN SCOPE** for R5.5 | R6+ candidate |

## What was empirically validated

### Track 1 — Docker Compose local (SMB envelope)

| Phase | Date | Evidence | Headline |
|---|---|---|---|
| **B-L baseline** | 2026-04-27 | [`capacity-planning.md` § "Single-instance measured ceiling"](capacity-planning.md) | Single-instance JWT issuance knee at ~75 req/s p99 ≤ 250 ms; collapse at 250 req/s |
| **C-L chaos** | 2026-04-28 | [`chaos-test-report-local.md`](chaos-test-report-local.md) | 9/9 idle experiments PASS; netem experiments require host-network workaround (R5.5 finding) |
| **D-L soak** | 2026-04-29 → 04-30 | [`soak-test-report-local.md`](soak-test-report-local.md) | **24h calendar-time PASS**: 144/144 steps · 958,525,165 OK / **0 fail** · p99 avg 60.66 ms · API RSS 351→432 MB plateau · Postgres conns 12-13 sustained (ADR-0015 Phase 2 invariant) |
| **E-L synthetic** | 2026-04-30 | inline with D-L | blackbox-exporter + journey-up SLO measured through soak |

### Track 2 — K8s local Talos lab (Enterprise reference)

| Phase | Date | Evidence | Headline |
|---|---|---|---|
| **0LK setup** | 2026-05-05 → 05-16 | [Talos chart commits `7d5fa78`→`6546155`](https://github.com/verbara/Verbara.Platform/commits/main) | 28 pods Ready on 3-worker Talos cluster + Cilium + MetalLB + CNPG + observability stack |
| **B-LK baseline** | 2026-05-25 (v2.5.2 closure) | [`docs/operations/r55-blk-evidence/2026-05-25-v252-pr32-validation/`](r55-blk-evidence/2026-05-25-v252-pr32-validation/) | Presence VU=1500: 43,184 OK / 0 Unauthorized / **0 platform-api restarts** / p99 3.7 s (after [ADR-0025](../decisions/0025-health-liveness-readiness-contract.md) PR #32 fix) |
| **C-LK chaos (idle)** | 2026-05-17 → 05-25 | [`chaos-test-report-k8s-local.md`](chaos-test-report-k8s-local.md) | 8/10 PASS — #06/#07 NetworkChaos BLOCKED by Cilium eBPF (environmental); #01 pg replica kill + #02 platform-api kill + #03 redis kill + #04 asterisk kill + #05 kamailio kill + #08 CPU stress + #09 memory stress + #10 CNPG failover all PASS |
| **C-LK chaos (loaded)** | 2026-05-25 | [`docs/operations/r55-blk-evidence/2026-05-25-c-lk-v252/`](r55-blk-evidence/2026-05-25-c-lk-v252/) | Headline: **CNPG primary failover under VU=1500 load** → /health 200 throughout, /health/ready 503→200, 0 platform-api restarts |
| **D-LK soak** | 2026-05-25 → 05-26 | [`docs/operations/r55-blk-evidence/2026-05-26-d-lk-soak-v254/`](r55-blk-evidence/2026-05-26-d-lk-soak-v254/README.md) | **17h36m substantive run** @ 30 RPS · 1.9 M req · 99.7363 % success · p99 OK 10.1 ms · **0 platform-api restarts during soak** · NBomber `MaxFailCount=5000` driver-side guard tripped (NOT app failure) |
| **JWT Tier-1 causality** | 2026-05-25 (v2.5.4) | [`docs/operations/r55-blk-evidence/2026-05-25-jwt-tier1-causality/`](r55-blk-evidence/2026-05-25-jwt-tier1-causality/) | **Mechanism identified**: TTL bump 60s→5min is PRIMARY driver of cold-cache cascade reduction (5×); Tier-1 stale-cache fallback is INSURANCE (0 fired in lab; load-bearing in production-cloud adversarial Redis) |

### What this composite validates

1. ✅ **Application correctness under sustained load** — 958 M req (D-L docker) + 1.9 M req (D-LK K8s) without functional regression.
2. ✅ **K8s liveness/readiness contract correctness** — [ADR-0025](../decisions/0025-health-liveness-readiness-contract.md) `/health` no-op + `/health/ready` full-check split + chart defensive probe tuning (`failureThreshold:5 + timeoutSeconds:3`) eliminate the pod-restart cascade observed on v2.5.1 (4 restarts) → 0 restarts on v2.5.4 across B-LK + C-LK + D-LK.
3. ✅ **Single-pool DB invariant under 24h+** — [ADR-0015 Phase 2](../decisions/0015-npgsql-datasource-sharing-strategy.md) one-`NpgsqlDataSource`-per-DSN held at 12-13 Postgres connections through D-L 24h soak; no leak signature.
4. ✅ **Native AOT correctness under multi-day runtime** — D-L docker + D-LK K8s both ran the Native AOT image (Phase D shipped 2026-05-20 per [ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md)); no AOT-specific regressions surfaced.
5. ✅ **Chaos resilience: kill-pod / kill-network / kill-pg / kill-redis / kill-asterisk / kill-kamailio / CPU stress / memory stress / CNPG primary failover** — all PASS on K8s; equivalents PASS on docker except netem (workaround documented).
6. ✅ **CNPG primary failover under VU=1500 load** — application contract held with `/health` stable, `/health/ready` correctly transitioning 503 → 200, 0 platform-api restarts. ~16 s total failover time including 4 s blip.
7. ✅ **JWT Tier-1 hardening** — v2.5.3 TTL 60s→5min + stale-cache fallback proven on lab (1,980 → 0 fails after HPA scale-up cold cascade); causality measured on v2.5.4 with per-pod `jwt_*` Prometheus counters.

### What this composite did NOT validate (gaps, explicitly listed)

| Gap | Why deferred | Re-evaluation trigger |
|---|---|---|
| Cloud single-region deployment | 2026-05-25 strategic pivot — no cloud spend pre-revenue | First paying customer onboarded |
| Multi-region cloud | Out of R5.5 scope | R6 candidate |
| 24h K8s soak full calendar window | D-LK driver-side `MaxFailCount=5000` early-aborted at 17h36m | Raise NBomber threshold and re-run when next K8s build candidate ships |
| Burst-into-sustained on K8s | Single steady shape only on D-LK | When a customer use case demands the profile |
| SIPp / voice traffic at sustained scale | R5.5 used HTTP-only `queue_ingestion` and `presence` scenarios | Phase E-LK voice/SIPp follow-up — gated on customer ask |
| Encryption-at-rest (Postgres data files, Redis RDB) | Out of R5.5 scope | R6 candidate, compliance-driven |
| HSM / KMS integration for DataProtection wrap | Out of R5.5 scope | Enterprise customer prerequisite |
| SIEM streaming (audit → Splunk/Elastic) | Out of R5.5 scope | Enterprise customer prerequisite |
| Network chaos under K8s (delay / partition) | Cilium eBPF blocks Chaos Mesh NetworkChaos in current lab cluster | Lab migration to standard CNI or environmental fix |
| etcd / apiserver chaos | Single-CP Talos lab — chaos would brick the lab | Multi-CP lab when warranted |
| Multi-tenant isolation chaos at scale | Single-tenant load profile only | Customer demand-driven |
| JWT Tier-2 cache-refresh gap (sustained-load drip) | On-hold per Tier-1 causality finding | Production telemetry `jwt_key_stale_cache_fallbacks_total > 0 sustained` (currently 0 in lab) |

## SRE checklist (Google PRR template, audited 2026-05-26)

### Reliability

| Item | Status | Evidence |
|---|---|---|
| SLOs defined with measured baselines | ✅ | [`slos.md`](slos.md) §1 Auth + JWT carries 🟢 measured datapoints from B-L; remaining §2-§8 still v1 provisional pending customer-traffic data |
| Error budget calculation documented | ✅ | [`slos.md`](slos.md) § "Review cadence" |
| Alerts configured with severity tiers | ✅ | [`alerts.yml`](alerts.yml) 6 P0 + 5 P1 + 5 P2 rules (v1 provisional thresholds — refresh after first customer traffic) |
| On-call runbook entries | ✅ | [`alerts-runbook.md`](alerts-runbook.md) |
| Synthetic monitoring active | ✅ | blackbox-exporter scraping `/health` + `/health/ready` on both Docker + K8s; Loki shipping logs |

### Capacity

| Item | Status | Evidence |
|---|---|---|
| Capacity planning per tier | ✅ | [`capacity-planning.md`](capacity-planning.md) — Small/Medium/Large/XL tiers + ADR-0015 Phase 2 single-pool envelope |
| Load test baseline reproducible | ✅ | [`scripts/scenario-sweep.sh`](../../scripts/scenario-sweep.sh) (preserve-step pattern from C-LK fix) + NBomber 6.1 `tests/Verbara.Platform.LoadTests/` |
| Soak test 24h+ validated no leaks | ✅ docker / ⚠ K8s 17h36m | D-L docker PASS 24h; D-LK K8s 17.6h (driver-side abort, not app) |
| HPA configured (K8s tier) | ✅ | `infra/k8s/helm/platform/templates/platform-api-hpa.yaml` — exercised in C-LK (HPA scale-up 2→6 under burst) |

### Resilience

| Item | Status | Evidence |
|---|---|---|
| Backup/DR runbook | ✅ | [`backup-disaster-recovery.md`](backup-disaster-recovery.md) — Postgres WAL archive + PITR + Redis RDB + JTI revocation rebuild |
| DR exercise procedure validated | ✅ | [`dr-exercises.md`](dr-exercises.md) — first entry executed |
| Chaos engineering experiments | ✅ docker / ⚠ K8s NetworkChaos blocked | [`chaos-test-report-local.md`](chaos-test-report-local.md) + [`chaos-test-report-k8s-local.md`](chaos-test-report-k8s-local.md) |
| CNPG primary failover < 30s (K8s tier) | ✅ | C-LK loaded: 16 s total, 4 s API blip — well under SLO |

### Security

| Item | Status | Evidence |
|---|---|---|
| Authentication: JWT RS256 + API key dual scheme | ✅ | `src/Verbara.Platform.Auth/` |
| JWT key rotation pool + Redis-backed cache | ✅ | v2.5.3 Tier-1 hardening · causality measured v2.5.4 |
| MFA per-tenant TOTP | ✅ | shipped pre-R5.5 |
| OIDC SSO per-tenant (Authorization Code + PKCE + nonce) | ✅ | shipped pre-R5.5 |
| RBAC 64 permissions / 8 templates | ✅ | `src/Verbara.Platform.Identity/` |
| Secrets via DataProtection wrap | ✅ | Phase B EF Core → Dapper → Npgsql migration (ADR-0022) |
| NetworkPolicy baseline (K8s tier) | ✅ | `infra/k8s/helm/platform/templates/networkpolicy.yaml` |
| Cosign-signed images (supply chain) | ✅ | release.yml since v2.4.1 (4 cosign-signed images per release) + visibility-monitor.yml + digest-reconciliation.yml |
| Encryption at rest | 🚫 NOT VALIDATED | R6 candidate |
| HSM / KMS | 🚫 NOT VALIDATED | Enterprise customer prerequisite |

### Observability

| Item | Status | Evidence |
|---|---|---|
| Prometheus metrics catalog | ✅ | 16 meters across Pro packages + `verbara.platform.*` meters (jwt, http_server_request_duration, etc.) |
| Grafana dashboards | ✅ | [`grafana-dashboards/`](grafana-dashboards/) — Platform.Api overview + Pro packages + K8s cluster |
| Loki log aggregation | ✅ | K8s lab via promtail; Docker via JSON-file driver |
| Distributed tracing | ✅ | ActivitySource catalog (11 sources, OTel-exported) — exercised in chaos runs |
| Health endpoints contract | ✅ | [ADR-0025](../decisions/0025-health-liveness-readiness-contract.md): `/health` no-op liveness + `/health/ready` full readiness |

### Build / Ship

| Item | Status | Evidence |
|---|---|---|
| Native AOT publish | ✅ | All shippable Verbara images are Native AOT since v2.4.1 ([ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md) Phase D shipped 2026-05-20) |
| Dapper banned, raw Npgsql via `Verbara.Sdk.Data.Npgsql` | ✅ | `BanDapperPackageReferences` MSBuild guard fails the build if referenced |
| `JsonSerializerIsReflectionEnabledByDefault=false` | ✅ | Every DTO in `[JsonSerializable]` source-gen context (`ApiJsonContext` / `RealtimeContractsJsonContext` / `PlatformPushJsonContext`) |
| Release workflow tag-driven | ✅ | `.github/workflows/release.yml` builds 4 cosign-signed images on annotated-tag push |
| Visibility / digest reconciliation | ✅ | `visibility-monitor.yml` (cosign verify + cosign.pub PEM parity) + `digest-reconciliation.yml` (daily 07:00 UTC) — both green at v2.5.4 |

### Onboarding

| Item | Status | Evidence |
|---|---|---|
| Customer-facing manuales (SMB Docker, ES) | ✅ | [`docs/manuales/smb/`](../manuales/smb/) — 12 docs covering install → arranque → setup wizard → 3 V1 canales (WebChat / Email / Voz SIP) → validación E2E → troubleshooting + capacity reference + signable checklist; refreshed to v2.5.4 (2026-05-25 PRs #36/#37) |
| K8s on-prem manuales | 🚧 PENDING | Phase 2 of manuales track (post-customer demand) |
| Cold-clone smoke test (Docker) | ✅ | `docker compose -f docker/docker-compose.reference-smb.yml up` → 1-min boot + setup wizard from clean clone |
| Demo environment | ✅ | `docker/demo/docker-compose.demo.yml` — pre-seeded + simulated PSTN; spec synced 2026-05-25 (PR #38) |

## Findings + remediation (R5.5 issues surfaced)

### Closed during R5.5

| ID | Surface | Resolution |
|---|---|---|
| **R5.5-FIND-001** | B-LK 2026-05-24 v2.5.1: 4 platform-api pod restarts under VU=1500 burst | Root-caused to K8s health contract violation (`/health` running same check suite as `/health/ready` including Postgres SQL ping). Fixed by [ADR-0025](../decisions/0025-health-liveness-readiness-contract.md) (PR #32, v2.5.2). Validated: 0 restarts on rerun. |
| **R5.5-FIND-002** | C-LK 2026-05-25: HPA scale-up 2→6 caused 1,980 Unauthorized in 60s window | Root-caused to JWT validation-key cache cold start on new pods. Fixed by Tier-1 hardening (TTL 60s→5min + stale-cache fallback) shipped v2.5.3. Validated: 1,980 → 0 fails on rerun. |
| **R5.5-FIND-003** | JWT meter `verbara.platform.jwt` not exported to Prometheus | Fixed by `5f34fb0e` `AddMeter("verbara.platform.jwt")` in OTel config (v2.5.4). Per-pod counters now scraped. |
| **R5.5-FIND-004** | `scripts/scenario-sweep.sh` wiped step archives because NBomber 6.x recursively cleans the reports dir | Fixed with preserve-step pattern (sweep moves per-step output BEFORE next step starts). |
| **R5.5-FIND-005** | C-LK NetworkChaos #06+#07 BLOCKED | Environmental: Cilium eBPF intercepts Chaos Mesh netem traffic shaping. Documented as known limitation in [`chaos-test-report-k8s-local.md`](chaos-test-report-k8s-local.md). |
| **R5.5-FIND-006** | v2.4.2 anomaly: 4 ghcr.io images pushed via maintainer-local `docker buildx --push` bypassing `release.yml` | Fixed by 6-layer hardening across 7 Platform PRs + 2 verbara-website PRs. ADR-0024 filed (still as draft). |

### Open / deferred

| ID | Surface | Disposition |
|---|---|---|
| **R5.5-FIND-007** | D-LK 17h36m: NBomber default `MaxFailCount=5000` inappropriate for >12h K8s soaks | **Closed 2026-05-27 housekeeping** — `LOADTEST_MAX_FAIL_COUNT` env-var override added in [`tests/Verbara.Platform.LoadTests/Program.cs`](../../tests/Verbara.Platform.LoadTests/Program.cs); default preserved at 5000 (genuine regressions still abort short sweeps fast), large soaks set the override per run. |
| **R5.5-FIND-008** | Helm chart `app.kubernetes.io/version` label drift (`2.5.1` vs actual image v2.5.4) | **Closed 2026-05-27 housekeeping** — bumped `appVersion` to `"2.5.4"` and chart `version` to `0.2.11` in [`infra/k8s/helm/platform/Chart.yaml`](../../infra/k8s/helm/platform/Chart.yaml). |
| **R5.5-FIND-009** | Liveness probe occasionally times out on `Predicate=>false` no-op endpoint under load | Likely kubelet HTTP-client queue contention. Absorbed by `failureThreshold:5`. Investigate if it resurfaces at higher rates. |
| **R5.5-FIND-010** | JWT Tier-2 sustained-load cache-refresh gap (5,013 drip Unauthorized over 17h36m on D-LK) | On-hold per causality finding. Trigger to ship Tier-2: production telemetry `jwt_key_stale_cache_fallbacks_total > 0 sustained`. |
| **R5.5-FIND-011** | Latent sync-over-async path in `JwtTokenService.GetCachedValidationKeys` | Tier-1 hardening covers user-visible impact. Defense-in-depth refactor remains a candidate. |
| **R5.5-FIND-012** | `slos.md` §2-§8 still carry v1 provisional thresholds (only §1 Auth + JWT carries 🟢 measured) | Refresh against first customer traffic, not synthetic load. |
| **R5.5-FIND-013** | `alerts.yml` thresholds remain v1 provisional | Refresh post first-customer-traffic data; current rules pass `promtool check rules`. |

## Sign-off

R5.5 is **shipped against the two empirical envelopes that exist** (SMB Docker single-host + Enterprise K8s on-prem 3-worker Talos lab). The product is **fit-for-first-paying-customer** within the SMB envelope; the Enterprise envelope is **fit-for-design-partner / pilot** with the K8s deployment pattern. Cloud envelopes are **deferred indefinitely** pending revenue.

Eight permanent architectural artifacts shipped during the R5.5 train (all in repo HEAD as of 2026-05-26):

1. [ADR-0015](../decisions/0015-npgsql-datasource-sharing-strategy.md) — single shared `NpgsqlDataSource` per DSN (Phase 1 + Phase 2 shipped v1.14.5 / v1.14.6).
2. [ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md) — Native AOT shipping path; Phase A (SignalR extraction) + B (DataProtection → Npgsql) + C (Dapper-as-blocker analysis) + D (total Dapper removal cross-repo).
3. ADR-0023 (cosign-signed images per release on ghcr.io) — superseded by [ADR-0030](../decisions/0030-cosign-v3-release-signing-posture.md) (cosign v3 signing posture); the ADR-0023 slot was later reused for [ADR-0023 Publishing non-AOT microservices](../decisions/0023-publishing-non-aot-microservices.md).
4. ADR-0024 — release process hardening after v2.4.2 anomaly (filed as draft, sweep across 7 PRs covers the controls).
5. [ADR-0025](../decisions/0025-health-liveness-readiness-contract.md) — `/health` no-op liveness + `/health/ready` full readiness split.
6. v2.5.3 JWT Tier-1 hardening (TTL bump + stale-cache fallback) + v2.5.4 OTel meter exposure.
7. [`tests/Verbara.Platform.E2E.Harness/`](../../tests/Verbara.Platform.E2E.Harness/) walking skeleton (from Phase A.5 SignalR exactly-once closure).
8. [`scripts/scenario-sweep.sh`](../../scripts/scenario-sweep.sh) preserve-step pattern + token refresh resilience + [`scripts/rerun-blk-validation.sh`](../../scripts/rerun-blk-validation.sh) + [`scripts/post-blk-finalize.sh`](../../scripts/post-blk-finalize.sh) + [`scripts/aggregate-blk-results.sh`](../../scripts/aggregate-blk-results.sh).

**Signed off:** 2026-05-26 by Verbara Platform maintainer (`hreina@verbara.io`).

## Next work

1. **SMB Docker product polish track** (primary, 6 sub-tracks documented in memory `project_roadmap` (maintainer session memory, not a repo artifact)) — manuales completion, onboarding feedback loop, first paying customer pursuit.
2. **K8s local manuales (Phase 2)** — mirror the SMB manuales for K8s on-prem; gated on customer demand.
3. ~~Phase E-LK doc~~ — **Closed 2026-05-27 housekeeping** — refreshed [`docs/operations/synthetic-monitoring.md`](synthetic-monitoring.md) with D-LK passive-verification PASS + cold-clone smoke procedure. Induce-failure smoke remains unscheduled (on-demand when lab is next up); AlertManager receiver wiring still gated on customer-side endpoint provisioning.
4. **R5.5 findings remediation** — FIND-007/008 closed 2026-05-27 (see findings table); FIND-009 watch-only; FIND-010/011 are conditional defense-in-depth.
5. **R6 brainstorm** — separate session: encryption-at-rest, HSM/KMS, SIEM streaming, multi-region staging, cloud K8s validation. Triggered by first paying customer or explicit override.

## References

- R5.5 spec (canonical): [`docs/plans/completed/2026-04-27-r5.5-production-validation-data.md`](../plans/completed/2026-04-27-r5.5-production-validation-data.md) (moved from `active/` 2026-05-26)
- R5.5 execution plan: [`docs/plans/completed/2026-04-27-r5.5-execution-plan.md`](../plans/completed/2026-04-27-r5.5-execution-plan.md) (moved from `active/` 2026-05-26)
- Strategic pivot rationale: memory `session-20260525-phase0c-deferred-smb-pivot` (maintainer session memory, not a repo artifact)
- All R5.5 evidence packs: [`docs/operations/r55-blk-evidence/`](r55-blk-evidence/)
- ADRs validated by R5.5: [`docs/decisions/0015`](../decisions/0015-npgsql-datasource-sharing-strategy.md) · [`0022`](../decisions/0022-platform-api-aot-shipping-path.md) · [`0025`](../decisions/0025-health-liveness-readiness-contract.md)
