# K8s Lab Licensing Setup — Reference Deployment Guide

**Authored:** 2026-05-18 (post Platform v2.3.1 deploy)
**Audience:** Maintainers deploying Verbara Platform to internal K8s labs OR customers running on-premise K8s with digest-pinned, cosign-signed images.
**Pro consumer pin:** v2.4.1-pro (with [LicenseTrustAnchor DI race fix v2.3.1](../../CHANGELOG.md#231-—-2026-05-18))
**Image integrity:** cosign signature + digest pin (Pro/ADR-0011). Licences issued since 2026-10-03 carry no image digests (verbara-meta/PDR-0003, amendment of 2026-10-03)
**Licensing mode:** Pro v2.4.0-pro canonical (`Licensing__FilePath`, no `EnforcementMode`)

This guide documents the **end-to-end reproducible procedure** to deploy `ghcr.io/verbara/platform/api:vX.Y.Z` to a K8s cluster with a real signed Pro license and a digest-pinned image. The procedure was validated on 2026-05-18 against the maintainer's Talos lab (1 CP + 3 workers, Cilium eBPF, CNPG Postgres, kube-prometheus-stack).

For SMB single-host docker-compose deployment, see [`docs/manuales/smb/01-instalacion-docker.md`](../manuales/smb/01-instalacion-docker.md). For the licensing semantics rationale see [Pro ADR-0012](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md).

---

## Prerequisites

### Tools (host that deploys)

| Tool | Min version | Why |
|---|---|---|
| `kubectl` | matches cluster | Cluster access |
| `helm` | 3.14+ | TryAddOwnership support (not strictly needed; 3.x suffices for most flows) |
| `crane` | latest from go-containerregistry | Insecure-registry push that preserves manifest digest (`go install github.com/google/go-containerregistry/cmd/crane@latest`) |
| `dotnet` SDK | 10.0.x | To run `Verbara.Sdk.Pro.LicenseGenerator` CLI for license issuance + validation |
| `jq` | any | Manifest parsing |

### Cluster prerequisites

- Verbara namespace target (e.g. `r55-platform`) exists with PodSecurity `restricted:latest` admission and corresponding ResourceQuota
- CNPG cluster running with a `platform` user + the canonical database name (`verbara` post-rebrand; legacy K8s deployments still use `asterisk_platform` — see [DB rename train](#db-rename-train) section below)
- Redis 8 StatefulSet reachable from the namespace (typically `redis-0.redis.r55-data.svc.cluster.local:6379`)
- kube-prometheus-stack Grafana + Prometheus running for observability (see [`docs/operations/grafana-licensing-panels.md`](grafana-licensing-panels.md))
- Container image registry reachable from cluster nodes (per Pro/ADR-0011 + ADR-0018, canonical is `ghcr.io/verbara/platform/api`; KVM labs mirror to `192.168.122.1:5050/verbara-platform/api` via `crane copy`)

### License signing key

The Verbara license trust anchor (`LicenseTrustAnchor.OfficialPublicKey`) is baked into every Pro Licensing binary since v2.2.0-pro. The corresponding **private key** lives in:
- Cloudflare Pages environment variable `VERBARA_LICENSE_SIGNING_KEY` (production developer-license issuer)
- Maintainer's local machine (offline backup for lab + sales-issued licenses) — path **NOT** documented here; the maintainer manages this manually

If you do not have access to the production signing key, request a Tier 0.5 Developer license via `https://verbara.io/developer-license` instead; it is issued unattended and, like every licence issued since 2026-10-03, carries no image digests. The digests to pin are in the release notes and the release ledger [`verbara-website/data/authorized-digests.json`](https://github.com/verbara/verbara-website/blob/main/data/authorized-digests.json).

---

## Procedure (end-to-end, ~25 minutes)

### Step 1 — Identify the target image manifest digest

```bash
# For each released version, the digest is registered in:
# https://github.com/verbara/verbara-website/blob/main/data/authorized-digests.json
#
# Fetch it programmatically from GHCR:
VERSION=v2.3.1
TOKEN=$(curl -s "https://ghcr.io/token?scope=repository:verbara/platform/api:pull&service=ghcr.io" | jq -r .token)
DIGEST=$(curl -s -H "Authorization: Bearer $TOKEN" \
  -H "Accept: application/vnd.docker.distribution.manifest.v2+json" \
  -H "Accept: application/vnd.oci.image.manifest.v1+json" \
  -D - "https://ghcr.io/v2/verbara/platform/api/manifests/$VERSION" -o /dev/null 2>&1 | \
  grep -i 'docker-content-digest' | tr -d '\r' | awk '{print $2}')
echo "VERSION=$VERSION"
echo "DIGEST=$DIGEST"
# Expected for v2.3.1: sha256:e0c876329fbeb24cfa1fd2c3a14da905ba45953ea12e19f8756b03b313fe0a8d
```

### Step 2 — Mirror the image to the cluster-reachable registry (KVM lab only)

If cluster nodes can reach `ghcr.io` directly, skip this step. The Talos lab cannot, so we mirror to the host KVM registry:

```bash
# crane copy preserves the manifest digest byte-for-byte, so the chart's digest pin
# (api.image.digest) matches the local registry pull.
crane copy ghcr.io/verbara/platform/api:$VERSION \
  192.168.122.1:5050/verbara-platform/api:$VERSION \
  --insecure

# Verify digest preserved (CRITICAL — must match $DIGEST from Step 1)
crane manifest 192.168.122.1:5050/verbara-platform/api:$VERSION --insecure | sha256sum
# Expected output: $DIGEST without the "sha256:" prefix, followed by "  -"
```

### Step 3 — Issue a Pro license

For maintainer-issued licenses (any tier). Since 2026-10-03 licences are issued
without image digests (verbara-meta/PDR-0003, amendment of 2026-10-03), so the licence
does not depend on `$VERSION` / `$DIGEST`:

```bash
cd /path/to/Verbara.Sdk.Pro

# Build the CLI from current source (or use `--no-build` if already built)
dotnet build tools/Verbara.Sdk.Pro.LicenseGenerator -c Release

# Issue (adjust --tier, --expires, --licensee per your scenario). WhiteLabel requires
# --max-agents: the licensed-agent band signed into the .lic (Pro >= the release after 2.16.2-pro).
dotnet run --project tools/Verbara.Sdk.Pro.LicenseGenerator -c Release --no-build -- --create \
  --licensee "Your Org Name — Lab" \
  --email "ops@example.com" \
  --tier WhiteLabel \
  --max-agents 25 \
  --expires 2027-05-18 \
  --private-key /path/to/your/private.pem \
  --output ./your-lab-$VERSION.lic

# Quick sanity check (validates the licence signature)
dotnet run --project tools/Verbara.Sdk.Pro.LicenseGenerator -c Release --no-build -- --validate \
  --license ./your-lab-$VERSION.lic
# Expected: VALIDATION RESULT : Valid
```

**Pro-tier guidance:**

| Tier | Use case | MaxAgents/Nodes | Lab fit? |
|---|---|---|---|
| `Developer` | Public free trial (Tier 0.5) — issued automatically by `verbara.io/api/developer-license` | 5 / 1 | Yes for evaluators; 30-day expiry forces hot-reload validation |
| `SelfHostStartup` | Small commercial deployments | 25 / 1 | Yes for small lab |
| `SelfHostBusiness` | Mid-market commercial | declared with `--max-agents` (required) / 10 | Yes for general lab |
| `WhiteLabel` | Top tier, all features unlocked | declared with `--max-agents` (required) / 0 | Best for "exercise everything" labs |

For Tier 0.5 via the public issuer (if you don't have the production signing key):

```bash
# Cloudflare Pages function — requires Turnstile token from the website widget
curl -X POST https://verbara.io/api/developer-license \
  -H "Content-Type: application/json" \
  -d '{"email":"ops@example.com","captchaToken":"<turnstile-token>"}'
# Response includes the signed .lic file as base64 — decode + save locally
```

### Step 4 — Create the K8s Secret holding the .lic file

```bash
kubectl create secret generic verbara-lab-license \
  --from-file=license.lic=./your-lab-$VERSION.lic \
  -n r55-platform

# Verify Secret content matches local file
kubectl get secret verbara-lab-license -n r55-platform \
  -o jsonpath='{.data.license\.lic}' | base64 -d | sha256sum
sha256sum ./your-lab-$VERSION.lic
# The two sha256sums MUST match.
```

**Note on rotation**: Pro v2.4.0-pro adds a `FileSystemWatcher` on `Licensing__FilePath`. Updating the Secret triggers an atomic K8s symlink swap (`license.lic → ..data/license.lic`) which the watcher detects, debounces 500 ms, and revalidates without pod restart. To rotate:

```bash
kubectl delete secret verbara-lab-license -n r55-platform
kubectl create secret generic verbara-lab-license --from-file=license.lic=./renewed.lic -n r55-platform
# Watch the validation log:
kubectl logs -n r55-platform -l app.kubernetes.io/name=platform-api --tail=10 -f | grep License
```

### Step 5 — Apply Helm chart with image override (KVM lab only)

The chart's `values.yaml` defaults to `ghcr.io/verbara/platform/api` (per Pro/ADR-0011). The KVM lab override + (if applicable) the legacy DB-name override go via `--set`:

```bash
cd /path/to/Verbara.Platform

# values.yaml MUST have api.image.tag + api.image.digest matching $VERSION / $DIGEST.
# Both are pinned in the committed chart, so no overrides needed for those.

helm upgrade platform infra/k8s/helm/platform -n default \
  --set api.image.repository=192.168.122.1:5050/verbara-platform/api \
  --set api.postgres.database=asterisk_platform  # legacy K8s DB name; remove after rename train
```

If your cluster pulls directly from ghcr.io (no KVM mirror), omit the `api.image.repository` override.

For the `database` override: see the [DB rename train](#db-rename-train) section. Customer deployments using the canonical `verbara` DB name need no override.

### Step 6 — Force the kubelet to retry pulling (skip backoff)

If the previous pod was in `ImagePullBackOff`, force a re-pull:

```bash
kubectl delete pod -n r55-platform -l app.kubernetes.io/name=platform-api \
  --field-selector='status.phase!=Running' --ignore-not-found
```

### Step 7 — Watch the rollout

```bash
kubectl rollout status deployment/platform-api -n r55-platform --timeout=240s
kubectl get pods -n r55-platform -l app.kubernetes.io/name=platform-api -o wide
```

Expected: 2/2 (or N/N for higher replica counts) `Running` 0 restarts within ~60 s.

### Step 8 — Post-deploy verifications

Run the following inside any platform-api pod:

```bash
POD=$(kubectl get pods -n r55-platform -l app.kubernetes.io/name=platform-api \
  -o jsonpath='{.items[0].metadata.name}')

# 1. License validation log (should say "is valid for ...")
kubectl logs $POD -n r55-platform --tail=200 | grep "is valid for"

# 2. ZERO event 12001 (EnforcementMode deprecation) — values.yaml drops the env
kubectl logs $POD -n r55-platform --tail=200 | grep -c 'event.id.*12001'
# Expected: 0

# 3. ZERO event 12002 (Production safety-net) — IMAGE_DIGEST is set
kubectl logs $POD -n r55-platform --tail=200 | grep -c 'event.id.*12002'
# Expected: 0

# 4. ZERO WorkerCrash logs (worker resilience hardening v2.4.1-pro + v2.3.0)
kubectl logs $POD -n r55-platform --tail=400 | grep -c 'WorkerCrash'
# Expected: 0

# 5. Health endpoint reachable
kubectl exec $POD -n r55-platform -- curl -s http://localhost:5000/health/ready | head -c 100
# Expected: JSON beginning with {"status":"...

# 6. IMAGE_DIGEST env matches the pinned digest (proves the chart wired api.image.digest)
kubectl exec $POD -n r55-platform -- sh -c 'echo "$IMAGE_DIGEST"'
# Expected: $DIGEST (same as Step 1)

# 7. License file mount + symlink (K8s atomic-swap pattern for hot-reload)
kubectl exec $POD -n r55-platform -- sh -c 'ls -la /etc/verbara/'
# Expected: license.lic -> ..data/license.lic (symlink), ..data -> ..YYYY_MM_DD_HH_MM_SS.* (symlink)
```

If any verification fails, see [Troubleshooting](#troubleshooting) below.

---

## What this deploy validates

The end-to-end procedure exercises **every defense layer** in Verbara's open-core licensing architecture:

| Layer | Mechanism | Validation in this guide |
|---|---|---|
| **A — Apache 2.0 OSS gate** | Pro features wrapped in `LicenseGuard.CanExecute(LicenseFeature)` | Pro feature endpoints accessible when license valid; HTTP 402 RFC 9457 when not |
| **B — Cosign signed images** | All v2.x images signed via Sigstore keyless OIDC in GitHub Actions | Image pulled by digest from `verbara-website/data/authorized-digests.json` |
| **C — Digest pin** | `api.image.digest` makes the kubelet pull those exact bytes and is exported as `IMAGE_DIGEST` (Pro/ADR-0011). Licences issued since 2026-10-03 carry no image digests; a licence issued before then that lists digests is still checked against the running image | Step 8 check 6 |
| **D — ECDSA license signature** | License signed by maintainer's private key, verified against `LicenseTrustAnchor.OfficialPublicKey` baked in Pro | Validated at boot + every 6h via `LicenseRevalidationService` |
| **E — Worker resilience** | ADR-0021 + Pro v2.4.1-pro — every BackgroundService outer-try-catch + `BackgroundServiceExceptionBehavior.StopHost` wired | Any silent worker death surfaces as pod restart + Critical log |

---

## DB rename train

The K8s lab deployed in 2026-04 / 2026-05 era still uses the pre-rebrand database name `asterisk_platform`. The chart's `values.yaml` was updated to `verbara` in the rebrand commit (118a48ae) but the cluster DB was never renamed. To migrate (one-time, ~5 minutes including downtime ~30 s):

```bash
# 1. Scale platform-api to zero (closes all open connections)
kubectl scale deployment platform-api -n r55-platform --replicas=0
kubectl wait --for=delete pod -n r55-platform \
  -l app.kubernetes.io/name=platform-api --timeout=60s

# 2. Find CNPG primary
PRIMARY=$(kubectl get pods -n r55-data -l cnpg.io/cluster,role=primary \
  -o jsonpath='{.items[0].metadata.name}')
echo "Primary: $PRIMARY"

# 3. Rename the database
kubectl exec -n r55-data $PRIMARY -- psql -U postgres \
  -c "ALTER DATABASE asterisk_platform RENAME TO verbara;"

# 4. helm upgrade WITHOUT the override (chart canon wins)
helm upgrade platform infra/k8s/helm/platform -n default \
  --set api.image.repository=192.168.122.1:5050/verbara-platform/api
# (drops the previous --set api.postgres.database=asterisk_platform)

# 5. Scale back + watch rollout
kubectl rollout status deployment/platform-api -n r55-platform --timeout=180s
```

After the rename, subsequent helm upgrades no longer require the `database` override.

---

## Troubleshooting

### Pod stuck in `ImagePullBackOff`

The local KVM registry doesn't have the image. Run Step 2 (crane copy) again, then delete the stuck pod to skip kubelet backoff:

```bash
kubectl delete pod -n r55-platform -l app.kubernetes.io/name=platform-api \
  --field-selector='status.phase!=Running'
```

### Pod crashes with `Npgsql.PostgresException 08P01: server_login_retry`

PgBouncer entered backoff after repeated auth failures. Most common cause: connection string targets a database name that doesn't exist in the backend (rebrand-era mismatch). See [DB rename train](#db-rename-train).

### Pod crashes with `LicenseException: ... has an invalid signature` but local `--validate` says Valid

**Fixed in Platform v2.3.1.** Pre-fix, Platform.Api unconditionally registered `Array.Empty<byte>()` as `byte[]` singleton, which shadowed Pro's default `LicenseTrustAnchor.OfficialPublicKey`. If you're on v2.3.0 or earlier, upgrade to >= v2.3.1. The trust anchor is always the embedded `LicenseTrustAnchor.OfficialPublicKey`; current releases do not read `Licensing__PublicKeyPath`.

### Pod crashes with `LicenseException: UnauthorizedImage`

The licence lists image digests and the IMAGE_DIGEST env var injected by the chart is not among them. Only a licence issued before 2026-10-03 lists digests; licences issued since carry none. Verify first that the pod runs the image you meant to deploy:

```bash
kubectl exec $POD -n r55-platform -- sh -c 'echo $IMAGE_DIGEST'
# Compare against:
crane manifest 192.168.122.1:5050/verbara-platform/api:$VERSION --insecure | sha256sum
```

If they don't match, the `crane copy` step did NOT preserve the manifest (rare — happens when `--platform` flag is omitted on a multi-arch source). Add `crane copy --platform linux/amd64 ...` to force.

If they match, the licence predates the image: request a current licence from Verbara (or, for a maintainer lab, issue one as in Step 3) and rotate the Secret as in Step 4.

### `presence-fanout: Degraded (heartbeat stale 45s > 30s)`

By-design behavior of Pattern B Rx-driven workers in v2.4.1-pro — the heartbeat only updates on event arrival. An idle subscription (no presence transitions for >30s) reports stale. The pod is healthy; the health check is over-conservative for idle Rx workers. Phase G-PRE in Pro v2.4.2-pro adds pre-start/idle/faulted differentiation to fix the false positive.

---

## Related documentation

- [`CHANGELOG.md`](../../CHANGELOG.md) — Platform release history including v2.3.1 LicenseTrustAnchor fix
- [`docs/decisions/0011-image-digest-binding-in-license-keys.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md) — ADR-0011 image-digest binding (Pro side)
- [`docs/decisions/0021-stophost-on-worker-crash-house-style.md`](../decisions/0021-stophost-on-worker-crash-house-style.md) — ADR-0021 worker resilience (Platform side)
- [`docs/operations/grafana-licensing-panels.md`](grafana-licensing-panels.md) — Licensing observability dashboard (Phase 5.2)
- [`docs/operations/prometheus-licensing-alerts.md`](prometheus-licensing-alerts.md) — Licensing alert rules (Phase 5.3)
- [`docs/operations/soak-test-report-k8s-local.md`](soak-test-report-k8s-local.md) — D-LK 24h soak report 2026-05-17/18

---

## Change log

- **2026-05-18**: Initial publication after Platform v2.3.1 lab deploy validation. Procedure validated end-to-end on Talos lab (1CP + 3 workers, Cilium eBPF). Authored by the maintainer post-deployment.
- **2026-10-29**: Licences issued since 2026-10-03 carry no image digests (verbara-meta/PDR-0003, amendment of 2026-10-03). Step 3 issues and validates without them; the defense-layer table and the `UnauthorizedImage` troubleshooting entry say what the digest pin does instead.
