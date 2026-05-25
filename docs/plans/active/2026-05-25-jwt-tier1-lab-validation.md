# Plan — Tier-1 JWT hardening lab validation (v2.5.3 release)

**Date:** 2026-05-25
**Status:** Ready for execution (next session)
**Owner:** Platform team
**Estimated effort:** 1 session (~3-4h)
**Predecessor commits:** `a6927f3a` (Tier-1 fix), follow-on observability commit (this session)

## Why this plan exists

Tier-1 hardening (stale-cache fallback + `ActiveKeyCacheTtl 60s → 300s` + observability counters) has been merged to main but **has not been validated under realistic chaos**. Unit tests cover the catch-path logic; the actual blast-radius reduction needs measurement against the same scenario that surfaced the failure — R5.5 Phase C-LK.2b with NBomber `presence` VU=1500 sustained 300s on Talos lab.

The unanswered question: **how much does Tier-1 actually reduce the 1,980 Unauthorized observed on v2.5.2 unmodified?** And: **does the new observability surface the events we expect (cache misses, stale fallbacks)?**

This plan is the empirical loop closing on the JWT Tier-1 work shipped 2026-05-25.

## Acceptance criteria

| Metric | Pre-Tier-1 (v2.5.2 C-LK measurement) | Post-Tier-1 target (v2.5.3) | Hard floor |
|---|---|---|---|
| NBomber `presence` VU=1500 / 300s fail count | 1,980 Unauthorized | **< 500** | < 1,000 |
| Fail rate | 1.01% | **< 0.3%** | < 0.5% |
| `jwt.key.cache_misses` counter | (no observability) | ≥ 5 events during sweep | ≥ 1 |
| `jwt.key.stale_cache_fallbacks` counter | (no observability) | ≥ 0 (may be 0 if cold-start dominates) | n/a |
| `jwt.key.fail_closed` counter | (no observability) | Roughly equals failed cold-start pods × 1 | n/a |
| HPA scale-up replicas observed | 2 → 6 | 2 → 6 | unchanged |
| Pod restart count delta | 0 | 0 | 0 (any regression here is a P0) |

**PASS criteria:** All "target" metrics met. Ship v2.5.3 to production.

**PARTIAL criteria:** All "hard floor" metrics met BUT some "target" missed. Document the residual, ship v2.5.3 as planned, **escalate Tier-2 priority** (now blocking-quality, not 2-week budget).

**FAIL criteria:** Any "hard floor" missed. **Revert Tier-1 stale-cache fallback** (the TTL bump + observability stay; both are net-positive even on rollback). Re-investigate.

## Pre-flight

- [ ] Verify main HEAD includes both `a6927f3a` (Tier-1 fix) and the observability commit (this session's commit)
- [ ] Verify lab is on v2.5.2 (cleanup state from previous C-LK closure)
- [ ] Verify no chaos objects active: `kubectl get podchaos,networkchaos,stresschaos -A`
- [ ] Verify CNPG cluster healthy: `kubectl get cluster -n r55-data postgres`
- [ ] Capture T0 hardware: pods, restart counts, HPA, CNPG, Prometheus alerts (use the existing `scripts/rerun-blk-validation.sh` pattern)

## Phase 1 — Release v2.5.3

1. **Bump version** in `Directory.Build.props`: `2.5.2 → 2.5.3`
2. **Commit version bump** with conventional message: `chore(release): bump to v2.5.3 — JWT Tier-1 hardening + observability`
3. **Tag annotated** `v2.5.3` with release notes referencing:
   - `a6927f3a` Tier-1 stale-cache fallback + TTL bump
   - Observability commit hash (this session)
4. **Push tag** → triggers `release.yml` workflow on GitHub Actions
5. **Wait for release.yml** to publish 4 cosign-signed images: `api`, `realtime`, `renderer`, `mail` at `ghcr.io/verbara/platform/<svc>:v2.5.3`
6. **Capture digests** from the GitHub Release page
7. **Update `verbara-website/data/authorized-digests.json`**: add v2.5.3 api + realtime to `current[]`, demote v2.5.2 to `deprecated[]`. Open PR + merge.
8. **Wait for daily reconciliation** (07:00 UTC) OR force-run `digest-reconciliation.yml` to confirm authorized status
9. **Update Helm chart values** in `infra/k8s/helm/platform/values.yaml`: bump api + realtime image tags + digests to v2.5.3. Open PR + merge.

**Gate:** v2.5.3 fully authorized + chart updated before proceeding to lab upgrade.

## Phase 2 — Lab upgrade

1. **Helm upgrade** Talos lab:
   ```bash
   helm upgrade platform infra/k8s/helm/platform -n default \
     --reuse-values \
     --set api.image.tag=v2.5.3 \
     --set api.image.digest=<api-digest> \
     --set realtime.image.tag=v2.5.3 \
     --set realtime.image.digest=<realtime-digest>
   ```
2. **Wait for rollout**: `kubectl rollout status deployment/platform-api -n r55-platform --timeout=180s`
3. **Verify pods**: 2/2 ready, 0 restart counts, image hash matches
4. **Smoke test** authenticated endpoint: `curl -H "Authorization: Bearer $TOKEN" http://api.r55.local/api/v1/admin/agents` → 200

**Gate:** Lab on v2.5.3 + smoke 200 before NBomber sweep.

## Phase 3 — C-LK.2b rerun (the measurement)

1. **Capture T0 snapshot**: `docs/operations/r55-blk-evidence/<UTC-date>-jwt-tier1-validation/T0.txt` (pods + restart counts + HPA + CNPG state + Prometheus alerts + Loki query baseline)
2. **Launch NBomber sustained**:
   ```bash
   PLATFORM_API_URL=http://api.r55.local SCENARIO_SWEEP_DURATION_SEC=300 \
     bash scripts/scenario-sweep.sh presence 1500 2>&1 | \
     tee docs/operations/r55-blk-evidence/<UTC-date>-jwt-tier1-validation/sweep-stdout.log
   ```
3. **Concurrently scrape `verbara.platform.jwt` meter** for 5 minutes:
   - Port-forward Prometheus or use `kubectl exec` to curl `/api/v1/query?query=jwt_key_cache_misses_total`
   - Capture per-pod counter trajectories for `jwt.key.cache_misses`, `jwt.key.stale_cache_fallbacks`, `jwt.key.fail_closed`
   - Save raw responses to `docs/operations/r55-blk-evidence/<UTC-date>-jwt-tier1-validation/metrics.txt`
4. **Concurrently capture Loki** for log events:
   ```bash
   kubectl logs -n r55-platform -l app.kubernetes.io/name=platform-api --tail=-1 --since=10m | \
     grep -E "EventId.\":\s*61[0-9]{2}" > \
     docs/operations/r55-blk-evidence/<UTC-date>-jwt-tier1-validation/logger-events.txt
   ```
5. **Capture T1 snapshot** + NBomber report (mirror C-LK.2b methodology)

## Phase 4 — Analysis + acceptance gate

1. **Compute fail rate**: NBomber report `fail count / total request count`. Compare against table above.
2. **Verify observability worked**:
   - `jwt.key.cache_misses` > 0 (proves the path is exercised)
   - `jwt.key.fail_closed` > 0 OR < `jwt.key.cache_misses` (proves cold-start events captured)
   - Loki should have `EventId: 6101/6102` (debug — may not surface unless log level lowered) + `EventId: 6103` (warning) + `EventId: 6104` (error)
3. **Walk through C-LK report finding #3** ("HPA-induced JWT cold-cache cascade"): annotate with v2.5.3 measurement deltas
4. **Decide gate**: PASS / PARTIAL / FAIL per criteria above

## Phase 5 — Documentation closure

1. **Append v2.5.3 rerun section** to `docs/operations/chaos-test-report-k8s-local.md` with the measured deltas
2. **Update `docs/research/2026-05-24-jti-investigation-presence-vu1500.md`** § Tier 1 with the empirical PASS/PARTIAL/FAIL outcome
3. **Update `docs/specs/2026-05-25-jwt-tier-2-redis-set-index.md`** acceptance-criteria table with the actual post-Tier-1 numbers (replaces the "estimate" column)
4. **Update memory** (`project_c_lk_validation_v252.md` + `project_current_position.md`) with closure
5. **Move this plan** `git mv docs/plans/active/2026-05-25-jwt-tier1-lab-validation.md docs/plans/completed/`
6. **Decide on Tier-2 priority**:
   - PASS → Tier-2 stays at "ship in 2 weeks" (active concern but not blocking)
   - PARTIAL → Tier-2 escalated to "blocking next release" (ship in 1 week)
   - FAIL → revert Tier-1 stale-cache fallback + emergency Tier-2 sprint

## Cross-references

- Tier-1 commits: `a6927f3a` + this session's observability commit
- Tier-1 spec evidence: [JTI investigation 2026-05-24](../research/2026-05-24-jti-investigation-presence-vu1500.md) § Tier 1
- Tier-2 spec: [JWT key store SET-index migration](../specs/2026-05-25-jwt-tier-2-redis-set-index.md)
- Phase C-LK baseline measurement: [Chaos test report K8s local](../operations/chaos-test-report-k8s-local.md) § "v2.5.2 rerun"
- ADR-0011 image-digest binding (governs Phase 1 ceremony)
- ADR-0023 4-image release model (governs release.yml workflow)
- ADR-0024 retroactive-tag guard (don't use; this is a forward release)
