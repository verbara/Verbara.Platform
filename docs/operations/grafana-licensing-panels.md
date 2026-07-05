# Grafana — Verbara Pro Licensing observability dashboard

**Authored:** 2026-05-18 (Phase 5.2 of v2.3.1 deploy follow-up)
**Dashboard file:** [`grafana-dashboards/verbara-licensing.json`](grafana-dashboards/verbara-licensing.json)
**Uid:** `verbara-licensing`
**Pairs with:** [`prometheus-licensing-alerts.md`](prometheus-licensing-alerts.md) (Phase 5.3)

This dashboard surfaces the `Verbara.Sdk.Pro.Licensing.Guard` OpenTelemetry meter signals + log-derived events from the Pro Licensing subsystem. Designed to run during the 6-week observability window (or whatever compressed timeline is in effect) before Pro v2.5.0-pro execution.

---

## Metric sources

Pro Licensing exposes three instruments through the `Verbara.Sdk.Pro.Licensing.Guard` meter (registered automatically via `AddProLicensing()` + `AddVerbaraProOpenTelemetry()`). When scraped by Prometheus through the OpenTelemetry `/metrics` exporter, they appear as:

| Pro instrument | Prometheus metric | Type | Tags | Purpose |
|---|---|---|---|---|
| `license.guard.blocked` | `license_guard_blocked_total` | Counter | `feature`, `reason` | Each `LicenseGuard.CanExecute(...)` call that returns Allowed=false |
| `license.image_unauthorized` | `license_image_unauthorized_total` | Counter | `running_digest` (truncated to 16 chars), `authorized_count` | Image-digest mismatch detected by `LicenseValidator.Validate` (Pro/ADR-0011 Layer C breach) |
| `license.guard.grace_remaining_seconds` | `license_guard_grace_remaining_seconds` | Observable Gauge | `feature` | Seconds remaining in the configured grace window per feature |

The dashboard ALSO uses:

- `up{job="platform-api"}` from the platform-api ServiceMonitor — to detect pod-down conditions caused by license-induced crashloops
- `kube_pod_container_status_restarts_total` from kube-state-metrics — to surface pod restart cadence (correlated with `Last State Reason: Error` for ADR-0021 worker-death attribution)
- Loki log lines matching `LicenseValidationHostedService` / `LicenseRevalidationService` / `LicenseException` / `WorkerCrash` — for boot + revalidation event timeline

---

## Panels

The dashboard ships with 9 panels organized in 4 visual bands:

### Top row — single-value health indicators (24h windows)

1. **License image-unauthorized count (24h)** — non-zero = Pro/ADR-0011 Layer C breach detected (image digest mismatch). Stat panel; green at 0, red ≥ 1.
2. **License blocks (24h, total)** — counts ALL blocked `CanExecute` calls regardless of feature. Yellow ≥ 1, orange ≥ 100.
3. **Platform-api replicas Up** — indirect health signal: if license validation crashes the host (Enforce mode default for v2.4.x without override), pods crashloop + this drops to 0.
4. **Worker crash count (24h)** — Loki count of `WorkerCrash` log lines (ADR-0021 worker-resilience signal). Expected: 0.

### Middle row — block rate + grace remaining time-series

5. **Block rate per feature × reason (req/s)** — breakdown of `license_guard_blocked_total` by Pro feature (Dialer / EventStore / Analytics / etc.) and reason (NotLicensed / Expired / GraceExhausted / UnauthorizedImage / Revoked). Use to identify which features are being denied + why.
6. **Grace-period remaining per feature (seconds)** — `license_guard_grace_remaining_seconds` per feature. Reports the configured grace window when fully valid, decreases linearly as expiry approaches.

### Logs panel

7. **License validation events (boot + revalidation)** — Loki tail of `LicenseValidationHostedService` + `LicenseRevalidationService` + `LicenseException` + `WorkerCrash` log lines. Default 8-row height, auto-refresh.

### Bottom row — incident drill-down

8. **Recent image-unauthorized events (running_digest × authorized_count)** — table panel showing each `RecordImageUnauthorized` emission grouped by truncated digest + license's allow-list size. Expected: empty.
9. **Pod restart count over time** — `changes(kube_pod_container_status_restarts_total)` to correlate restarts with the WorkerCrash panel above.

### Variables

- `namespace` — multi-select dropdown auto-populated from `up{job="platform-api"}` labels. Useful when running parallel `r55-platform` + `r55-platform-v25-preview` namespaces during the compressed validation window.

---

## Installation

### Docker compose (development stack)

Copy the JSON into the existing grafana-provisioning bind mount:

```bash
cp docs/operations/grafana-dashboards/verbara-licensing.json \
   docker/observability/grafana-provisioning/dashboards/

# Restart Grafana (or wait for the auto-reload sidecar; the existing dashboard.yaml
# provider in docker/observability/grafana-provisioning has updateIntervalSeconds: 30)
docker compose -f docker/docker-compose.full.yml restart grafana
```

### K8s lab (kube-prometheus-stack via Helm)

The Grafana operator's sidecar discovers ConfigMaps with the `grafana_dashboard=1` label. Create one:

```bash
kubectl create configmap verbara-licensing-dashboard \
  --from-file=verbara-licensing.json=docs/operations/grafana-dashboards/verbara-licensing.json \
  -n monitoring \
  --dry-run=client -o yaml | \
  kubectl label --local -f - grafana_dashboard=1 -o yaml | \
  kubectl apply -f -

# Verify
kubectl get configmap verbara-licensing-dashboard -n monitoring \
  -o jsonpath='{.metadata.labels}'

# The sidecar polls every ~10 s; dashboard should appear in Grafana within ~30 s
# under tag "verbara" or by direct UID `verbara-licensing`.
```

To update the dashboard after editing the JSON:

```bash
kubectl create configmap verbara-licensing-dashboard \
  --from-file=verbara-licensing.json=docs/operations/grafana-dashboards/verbara-licensing.json \
  -n monitoring \
  --dry-run=client -o yaml | \
  kubectl apply -f -
```

(No need to re-label — labels persist across `apply`.)

---

## Per-panel interpretation cheatsheet

| Symptom | Possible cause | Where to look next |
|---|---|---|
| `image-unauthorized count` > 0 | An operator deployed an image whose manifest digest is NOT in `AuthorizedImageDigests`. Either license is stale (issued before current digest registered) or the wrong image was deployed. | Panel 8 (table) — find the truncated digest; reconcile against `verbara-website/data/authorized-digests.json` |
| `License blocks (24h, total)` very high + many `feature=NotLicensed` reasons | Customer hitting Pro endpoints without a valid license (could be the v2.5.0-pro behaviour we're validating, OR a misconfigured client polling) | Panel 5 — look at the feature×reason breakdown; if it's a single feature + low rate, probably exploratory client; if it's all features + high rate, license is genuinely missing |
| `Grace remaining` curve drops to 0 | License expired AND grace exhausted; subsequent calls return Allowed=false (Pro features now respond HTTP 402 RFC 9457) | Panel 7 (logs) — check for `LicenseException: ... expired at ...` + `LicenseRevalidationService` re-validation attempts |
| `replicas Up = 0` AND `License validation events` shows `LicenseException` at boot | Either: license file missing/corrupt; OR LicenseTrustAnchor DI race (pre-v2.3.1 bug, fixed); OR cluster lost license Secret mount | `kubectl get secret verbara-lab-license -n r55-platform` + `kubectl describe pod` + check pre-v2.3.1 in CHANGELOG |
| `Pod restart count` > 0 in a window where `Worker crash count` > 0 | Worker silent-death caught by `BackgroundServiceExceptionBehavior.StopHost` (ADR-0021). The hardening worked as designed. | Panel 7 → grep for `WorkerCrash` to attribute to specific worker; `kubectl logs <pod> --previous` for stack trace |
| `Pod restart count` > 0 BUT `Worker crash count = 0` | Restart unrelated to workers — could be liveness probe failure, OOM (kernel signal 137), or external SIGTERM | `kubectl describe pod` Events section — look for OOMKilled or Liveness Probe Failed |

---

## Observability window usage (compressed v2.5.0-pro path)

Per the compressed timeline plan ([roadmap](../roadmap.md)):

| Window | What to watch for | Acceptance gate |
|---|---|---|
| Days 0-2 post-deploy | Boot validation cleanup — `image_unauthorized_total` = 0, `blocks_total` initial baseline, license validation succeeds at every revalidation interval (6h default) | Steady state for ≥48 h |
| Days 3-7 | Hot-reload via FileSystemWatcher exercised (rotate the Secret + watch the LicenseRevalidationService re-validate without pod restart) | `License re-validation started` event appears within 500 ms debounce + no pod restart |
| Days 7-N (until v2.5.0-pro tag) | Scenario B/C/E rehearsal in `EnforcementMode=WarnOnly` preview namespace | All scenarios PASS per [`compressed-validation-report-v250pro.md`](compressed-validation-report-v250pro.md) when completed |

When all gates pass, the maintainer can amend ADR-0012 pre-condition #1 with evidence-based shortened window justification + execute Pro v2.5.0-pro train.

---

## Future enhancements (deferred)

- **License expiry countdown gauge** — add `license.expiry_seconds_remaining` Observable Gauge to Pro `LicenseGuardMetrics` (current Pro v2.4.1-pro lacks this; could land in v2.4.2-pro alongside Phase G-PRE)
- **LicenseStatusReader scrape** — synthetic probe via blackbox-exporter against `/management/system/license/status` to verify the admin endpoint serves the snapshot correctly
- **Per-tenant license metrics** — when Pro multi-tenancy moves to per-tenant licenses (no current ETA), add `tenant_id` tag dimension

---

## Related documentation

- [`k8s-lab-licensing-setup.md`](k8s-lab-licensing-setup.md) — deploy procedure that this dashboard observes
- [`prometheus-licensing-alerts.md`](prometheus-licensing-alerts.md) — alert rules that page on the same signals
- [Pro `LicenseGuardMetrics.cs`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/src/Verbara.Sdk.Pro.Licensing/Diagnostics/LicenseGuardMetrics.cs) — source of truth for instrument names + tags
