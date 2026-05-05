# Staging environment

> **R5.5 Production Validation** — this document captures the canonical staging
> setup used to drive load, chaos, and soak runs that materialize the
> v1-measured SLO + capacity numbers.
> Source plan: `docs/plans/active/2026-04-27-r5.5-production-validation-data.md`.
> Companion execution plan: `docs/plans/active/2026-04-27-r5.5-execution-plan.md`.

The staging environment ships in **two reproducible variants** that share most
configuration files and seed scripts:

| Variant | Target audience | When to use |
|---|---|---|
| **Docker Compose** (Phase 0L) | SMB customer ceiling reference; fast iteration on dev workstation | First pass — validates `getting-started.md` cold-clone path, scripts, seeds. |
| **K8s local — Talos + Kamailio/RTPEngine SBC** (Phase 0LK) | Enterprise customer reference; production-realism for SIP/RTP at scale | Second pass — validates Helm charts, CloudNativePG operator, Asterisk-on-K8s SBC pattern before cloud spend. |

Both variants run on the same dev workstation in parallel (Hardware fit
validated 2026-04-27 against AMD Ryzen 9 9900X / 60 GB RAM / 24 threads /
KVM-libvirt active). Cloud parity (Phase 0C) layers on top once both local
variants are green.

---

## Hardware baseline (commit machine, captured 2026-04-27)

| Resource | Reading |
|---|---|
| CPU | AMD Ryzen 9 9900X — 12 cores / 24 threads, vmx/svm on all 24 |
| RAM | 60 GB (≥ 35 GB free at idle) |
| Swap | 68 GB |
| Disk | `/` 168 GB free · `/home` 507 GB free (NVMe SSD) |
| Virt | KVM (`kvm_amd` loaded), `/dev/kvm` accessible, `libvirtd` active |
| Tooling | Docker 29.4 · kubectl 1.35.1 · helm 3.20 · minikube installed |
| Tooling pending | `kind` (smoke fallback) · `talosctl` (mandatory for 0LK) |

**RAM accounting target** (peak run with both variants up + observability):

| Stack slice | Approx footprint |
|---|---|
| Talos cluster (1 control + 3 workers × 2 GB) | ~10 GB |
| Asterisk + Kamailio/RTPEngine + CloudNativePG cluster | ~6 GB |
| K8s observability (kube-prometheus-stack + Loki) | ~4 GB |
| Docker Compose stack (asterisk + platform + postgres + redis + web + …) | ~8 GB |
| Compose observability side-stack (Prometheus + Grafana + Loki + exporters) | ~3 GB |
| Test runners (NBomber + SIPp + Pumba) | ~3 GB |
| **Peak total** | **≈ 34 GB** (leaves ~26 GB margin on 60 GB) |

---

## Docker Compose variant (Phase 0L)

**Setup:**

```bash
cd /path/to/Asterisk.Platform
# generate dev-only secrets (gitignored — never commit docker/.env)
test -f docker/.env || cat <<EOF > docker/.env
SERVICE_KEY=$(openssl rand -hex 32)
POSTGRES_PASSWORD=$(openssl rand -hex 24)
AMI_PASSWORD=$(openssl rand -hex 24)
ARI_PASSWORD=$(openssl rand -hex 24)
EOF

# 1) bring up the stack
docker compose -f docker/docker-compose.full.yml up -d --wait

# 2) bring up the observability side-stack
docker compose -f docker/docker-compose.observability.yml up -d --wait
```

The observability stack joins the existing `docker_default` external network
(the project network created by `docker-compose.full.yml`) so Prometheus can
resolve `platform-api:5000`, `postgres:5432`, etc. by service name.

### Services exposed

| Service | URL | Notes |
|---|---|---|
| Web UI | http://localhost:80 | React app — admin login, queue + agent admin |
| Platform.Api | http://localhost:5000 | OpenAPI: http://localhost:5000/scalar/v1 |
| Platform.Api `/health` | http://localhost:5000/health | Aggregated readiness probe |
| Platform.Api `/health/ready` | http://localhost:5000/health/ready | Per-check JSON (registered HCs) |
| Platform.Api `/metrics` | http://localhost:5000/metrics | OTel prometheus exposition |
| Asterisk AMI | tcp://localhost:5038 | Configurable via `AMI_PASSWORD` in `.env` |
| Asterisk ARI | http://localhost:8088 / wss://localhost:8089 | Configurable via `ARI_PASSWORD` |
| Asterisk SIP | UDP 5060 | RTP `udp/20000-20200` |
| Postgres | tcp://localhost:5432 | Internal-only — not bound to host by default |
| Prometheus | http://localhost:9090 | Reads `docs/operations/alerts.yml` as `rule_files` |
| Grafana | http://localhost:3000 | `admin / r55-staging` (dev-only) |
| Loki | http://localhost:3100 | Logs ingest, anonymous read for Grafana |
| Alertmanager | http://localhost:9093 | Console webhook receiver (no real notifier) |
| node-exporter | http://localhost:9100/metrics | Host CPU/mem/disk/net |
| blackbox-exporter | http://localhost:9115 | HTTP probes for synthetic monitoring |

### Verification (Phase 0L baseline)

```bash
# 1) all 6 app + 6 observability containers healthy
docker compose -f docker/docker-compose.full.yml ps
docker compose -f docker/docker-compose.observability.yml ps

# 2) Prometheus targets all `up`
curl -fsS http://localhost:9090/api/v1/targets \
  | python3 -c "import json,sys; d=json.load(sys.stdin); [print(t['labels']['job'], t['health']) for t in d['data']['activeTargets']]"

# 3) 15 alert rules loaded (5 P0 + 5 P1 + 5 P2 — ADR-0009)
curl -fsS http://localhost:9090/api/v1/rules \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(sum(len(g['rules']) for g in d['data']['groups']))"

# 4) Grafana dashboard provisioned under R5.5 folder
curl -fsS -u admin:r55-staging 'http://localhost:3000/api/search?type=dash-db' \
  | python3 -c "import json,sys; [print(x['title']) for x in json.load(sys.stdin)]"

# 5) Alert smoke test — PlatformApiUnavailable fires + resolves
docker stop $(docker ps -qf name=platform-api)
sleep 165   # 2m for: window + scrape margin
curl -fsSG http://localhost:9090/api/v1/query --data-urlencode 'query=ALERTS{alertname="PlatformApiUnavailable"}'
docker start $(docker ps -aqf name=platform-api)
sleep 60
curl -fsSG http://localhost:9090/api/v1/query --data-urlencode 'query=ALERTS{alertname="PlatformApiUnavailable"}'
# expect: ALERTS samples=1 firing, then 0 after restart
```

### Tear-down

```bash
docker compose -f docker/docker-compose.observability.yml down -v
docker compose -f docker/docker-compose.full.yml down -v
```

### P0 findings surfaced during Phase 0L bring-up (2026-04-27)

| Finding | Fix commit | Detail |
|---|---|---|
| `DatabaseMigrationService.Rollback` masking real migration errors when Postgres aborts the tx server-side | `b0e6100` | Wrapped `tx.Rollback()` in try/catch — secondary `InvalidOperationException` swallowed so the original SQL error surfaces. |
| Migration `021_AuditEntriesNormalize.sql` had explicit `BEGIN;` + `COMMIT;` while the C# runner already wraps each migration in its own tx — nested commit triggered `transaction is already completed` on the next Dapper.Execute | `b0e6100` | Removed `BEGIN`/`COMMIT` lines, added comment explaining the C#-side tx wrap. Only migration with this issue per `grep -l "^BEGIN" Migrations/*.sql`. |

Both bugs were R5.3 carry-overs that demo databases hid (already-applied state).
A fresh Phase 0L bring-up was the first scenario to exercise them.

---

## K8s local variant (Phase 0LK)

> **Status (2026-05-04):** **LIVE DEPLOYMENT VERIFIED.** All 28 workload
> pods running across 4 namespaces. Cold-bootstrap from powered-off VMs to
> full stack took ~40 min (cluster 15 min + apps 25 min). Container images
> served via host-local Docker registry (`192.168.122.1:5050`) with Talos
> insecure-registry patch (applied live, no reboot).

### Stack

| Layer | Component | Version |
|-------|-----------|---------|
| OS | Talos Linux (immutable) | 1.13.0 |
| K8s | Kubernetes | 1.36.0 |
| CNI | Cilium eBPF (replaces kube-proxy, MetalLB, Traefik) | 1.19.3 |
| LB | Cilium LB-IPAM + L2 announcements | (built-in) |
| Ingress | Cilium Gateway API v1.3.0 | (built-in) |
| Storage | Rancher local-path-provisioner | 0.0.30 |
| DB | CloudNativePG operator → Postgres 17 (3-instance HA) | CNPG 1.25.0 |
| DB pool | PgBouncer (CNPG Pooler CR, transaction mode) | (managed) |
| Cache | Redis 8 (StatefulSet, AOF persistence) | 8-alpine |
| PBX | Asterisk (StatefulSet, 2 replicas, anti-affinity) | 22 |
| SBC | Kamailio (DaemonSet, hostNetwork) | 5.8.8 |
| Media | RTPEngine (DaemonSet, hostNetwork) | fonoster/latest |
| API | Platform.Api (Deployment, HPA 2→8) | 1.14.6 |
| Web | Platform.Web (Deployment, nginx) | 1.15.5 |
| Monitoring | kube-prometheus-stack (Prometheus + Grafana + Alertmanager) | chart 84.5.0 |
| Logs | Loki (SingleBinary, 7d retention) | chart 6.55.0 |
| Probes | blackbox-exporter (7 targets) | chart 11.9.2 |
| Alerts | PrometheusRule CRD (17 rules: 7 P0 + 5 P1 + 5 P2) | — |

### Topology

| Node | IP | Role | RAM | vCPU | Disk |
|------|-----|------|-----|------|------|
| talos-cp1 | 192.168.122.10 | Control Plane | 4 GB | 2 | 20 GB |
| talos-w1 | 192.168.122.11 | Worker | 4 GB | 4 | 40 GB |
| talos-w2 | 192.168.122.12 | Worker | 4 GB | 4 | 40 GB |
| talos-w3 | 192.168.122.13 | Worker | 4 GB | 4 | 40 GB |

**Network:** libvirt `default` NAT on `192.168.122.0/24` with static DHCP reservations.
**LB IP pool:** `192.168.122.200/28` (Cilium L2 announcements).
**K8s API:** `https://192.168.122.10:6443`

### Namespaces

| Namespace | Contents |
|-----------|----------|
| `r55-data` | Postgres 3-HA + PgBouncer pooler + Redis |
| `r55-asterisk` | Asterisk StatefulSet + Kamailio DaemonSet + RTPEngine DaemonSet |
| `r55-platform` | Platform.Api Deployment + Web Deployment + Ingress |
| `monitoring` | kube-prometheus-stack + Loki + blackbox-exporter + PrometheusRule |
| `local-path-storage` | Rancher local-path-provisioner |

### Security hardening (shipped 2026-05-04)

| Feature | Scope | Detail |
|---------|-------|--------|
| PodDisruptionBudgets | Asterisk + Platform API | `minAvailable: 1` — protects during node drain/upgrades |
| NetworkPolicies | 17 policies, 4 namespaces | Default-deny ingress + service-level whitelists; standard `networking.k8s.io/v1` |
| ResourceQuotas | r55-data, r55-asterisk, r55-platform | Per-namespace CPU/memory/pod caps with headroom for HPA |
| SecurityContext | All workloads | `runAsNonRoot`, `seccompProfile: RuntimeDefault`, `allowPrivilegeEscalation: false` |
| Pod anti-affinity | Platform API | Soft scheduling spread across nodes (compatible with HPA) |
| Init containers | Platform API | `wait-for-postgres` DNS check prevents crash loops on cold start |
| Health probes | Kamailio, RTPEngine, Redis | Liveness + startup probes added (was missing); RTPEngine readiness added |
| CloudNativePG backup | Documented | barman/S3 config ready to uncomment when object store available |

**Known limitations:**
- Kamailio + RTPEngine use `hostNetwork: true` — NetworkPolicies do NOT apply to these pods
- `local-path-provisioner` does not support VolumeSnapshots — CNPG backup needs S3/MinIO
- Staging secrets hardcoded in templates (tagged `r55-staging-only-change-prod`) — production requires external secret management

### Bootstrap

```bash
scripts/k8s-up.sh
```

### Tear-down

```bash
scripts/k8s-down.sh --confirm
```

### Services exposed (via Cilium Gateway / port-forward)

| Service | Access | Notes |
|---------|--------|-------|
| Platform.Api | `api.r55.local` via Ingress | Health: `/health`, `/health/ready` |
| Web UI | `r55.local` via Ingress | nginx proxies `/api/` to platform-api:5000 |
| Grafana | `grafana.r55.local` via Ingress | `admin / r55-staging` |
| Prometheus | `kubectl -n monitoring port-forward svc/prometheus-kube-prometheus-prometheus 9090:9090` | |
| Asterisk SIP | hostNetwork on workers (UDP 5060) | Via Kamailio dispatcher |
| RTP media | hostNetwork on workers (UDP 10000-10500) | Via RTPEngine |

### Helm charts

| Chart | Path | Key resources |
|-------|------|---------------|
| `asterisk` | `infra/k8s/helm/asterisk/` | StatefulSet, 2 DaemonSets, ConfigMaps, Services |
| `platform` | `infra/k8s/helm/platform/` | Deployment ×2, HPA, Secrets, Ingress |
| observability | `infra/k8s/helm/observability/` | Values for 3 community charts + install.sh |

### Key architecture decisions

- **Cilium replaces 5 components** (Flannel, kube-proxy, MetalLB, Traefik, network observability) — see `infra/k8s/talos/README.md`.
- **PodSecurity `privileged`** label required on `r55-data`, `r55-asterisk`, `local-path-storage` namespaces (K8s 1.36 enforces "baseline" by default; local-path helper pods need hostPath, Kamailio/RTPEngine need hostNetwork).
- **Kamailio dispatches to ClusterIP** service `asterisk-sip` (not headless pod DNS) — resolves at config-load time, initContainer waits for DNS.
- **PgBouncer pooler** (CNPG Pooler CR) sits between API and Postgres — reduces failover window from 5-30s to 2-5s.
- **Production hardening sprint** (2026-05-04): PDBs, 17 NetworkPolicies, ResourceQuotas, SecurityContexts, probes — see § "Security hardening" above.
- **docker-compose.production.yml** logging rotation + resource limits applied — closes the disk-fill prevention gap for SMB tier production.

---

## Phase 0L verification log

### 2026-04-27

- [x] Hardware baseline documented (above)
- [x] Docker Compose stack healthy (6/6 containers)
- [x] Observability stack healthy (6/6 containers)
- [x] Prometheus 5/5 targets `up` (prometheus, platform-api, node-exporter, 2× blackbox-http probes)
- [x] 15 alert rules loaded across 3 ADR-0009 severity groups (P0=5, P1=5, P2=5)
- [x] Grafana dashboard `r55-overview` provisioned, datasources Prometheus + Loki configured
- [x] Alert smoke test — `PlatformApiUnavailable` fires after 2m down, resolves after restart, propagates to Alertmanager
- [x] 2 P0 migration runner findings surfaced + fixed (`b0e6100`)

---

## Phase 0LK verification log

### 2026-05-04 — Live deployment

- [x] Talos cluster 4/4 nodes Ready (K8s 1.36.0, Cilium 1.19.3, Gateway 192.168.122.192)
- [x] CloudNativePG Postgres 3-HA + PgBouncer pooler (2 replicas)
- [x] Redis StatefulSet (AOF persistence, securityContext hardened)
- [x] Asterisk 2-replica StatefulSet + Kamailio DaemonSet (3) + RTPEngine DaemonSet (3)
- [x] Platform.Api 2-replica Deployment — Postgres healthy, background workers running
- [x] Platform.Web nginx Deployment — serving static assets
- [x] kube-prometheus-stack (Prometheus + Grafana + Alertmanager + node-exporter 4/4)
- [x] Loki SingleBinary + blackbox-exporter
- [x] 35 PrometheusRule sets loaded (34 kube-prometheus-stack + 1 r55-platform-rules)
- [x] 17 NetworkPolicies across 4 namespaces
- [x] 3 ResourceQuotas (all within limits)
- [x] Prometheus scraping 13+ targets (apiserver, kubelet, coredns, grafana, kube-state-metrics, alertmanager, node-exporter all `up`)
- [x] 28 workload pods total, 0 crashloops

### Findings surfaced during live deployment

| Finding | Fix | Detail |
|---|---|---|
| Platform API `runAsNonRoot` fails — aspnet:10.0 image defaults to root | Added `runAsUser: 1654` (dotnet `app` user) | Container starts with correct UID without Dockerfile change |
| Platform API missing production env vars (`ServiceKey`, `CORS_ORIGINS`, AMI) | Added env vars to deployment template + values.yaml | ServiceKey, CORS, AMI/ARI, Licensing config required for production mode |
| Prometheus `/prometheus` permission denied with `fsGroup` + local-path-provisioner | Added initContainer `fix-permissions` (chown as root) | hostPath-based PVs do not honor fsGroup; initContainer is the standard workaround |
| Loki results-cache `Pending` — insufficient memory on 4 GB worker nodes | Disabled `resultsCache`, reduced `chunksCache` to 128 MB | SingleBinary mode doesn't benefit from memcached results-cache |
| node-exporter blocked by PodSecurity `baseline` enforcement | `kubectl label ns monitoring pod-security.kubernetes.io/enforce=privileged` | node-exporter requires hostNetwork + hostPID + hostPath |
| Container images need local registry — Talos has no `docker build` | Host registry `192.168.122.1:5050` + `talosctl patch` insecure-registry | `crane push --insecure` bypasses Docker daemon TLS requirement |
