# Plan C — Talos-lab migration `r55-platform` from Platform v2.3.1 → v2.4.3

> **Authored:** 2026-05-23 (Auto Mode)
> **Track:** ADR-0022 Phase A.5 closure — paired with Plan B (Talos smoke test) and the docker-compose Option-A smoke test already PASSED.
> **Status:** PROPOSED — maintainer-executed migration. Subagent describes; maintainer runs.
> **Repos touched:** [`Verbara.Platform`](../../../) (Realtime `Program.cs`, `Directory.Build.props`, helm `Chart.yaml`), [`verbara-website`](../../../../verbara-website/) (amend pending commit `01c455f`).
> **Image releases produced:** `ghcr.io/verbara/platform/{api,realtime,renderer,mail}:v2.4.3` (cosign-signed), local-registry mirror at `192.168.122.1:5050/verbara-platform/{api,realtime,renderer,mail}:v2.4.3`.
> **Source ADRs:** [Pro/ADR-0011 image-digest binding](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md) · [ADR-0022 AOT shipping path](../../decisions/0022-platform-api-aot-shipping-path.md) · [ADR-0023 publishing non-AOT microservices](../../decisions/0023-publishing-non-aot-microservices.md).

## 1. Goal & non-goals

### 1.1 Goal

Bring the maintainer's Talos lab (`r55-platform` workload namespace, helm release `platform` registered in `default` namespace) from the currently-deployed **v2.3.1 monolithic Platform.Api + Web** to **v2.4.3** — a hotfix on top of the already-tagged-locally v2.4.2 that closes Gap-1 from [`phase-a5-smoke-test-2026-05-23.md`](../../operations/phase-a5-smoke-test-2026-05-23.md): `Verbara.Platform.Realtime/Program.cs` does NOT invoke `Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync(...)` at startup, so the `cluster_distributed_lock` table is never created automatically. The hotfix wires that call in.

The migration also brings the four-microservice topology (Api + Realtime + Renderer + Mail) per [ADR-0023](../../decisions/0023-publishing-non-aot-microservices.md) live in the lab — today only Api + Web run in `r55-platform`.

### 1.2 Non-goals

- **NO production cluster touched.** This plan is scoped to one namespace (`r55-platform`) on one lab cluster (`admin@asterisk-platform`, the maintainer's 4-node Talos VM cluster).
- **NOT a release-process audit.** Phase 5 cutover + Phase 6 24h soak for `v2.4.1` are already DONE; v2.4.3 piggybacks on the existing build/sign/push pipeline.
- **NOT a chart audit.** The helm chart at HEAD already has the Realtime microservice templates wired (commit `91e9e6cc`) — this plan just exercises a fresh `helm upgrade` against the existing release.
- **NO data migration** beyond the new `cluster_distributed_lock` table the hotfix creates. The `_migrations` chain from v2.3.1 → HEAD added **zero new files** under [`src/Verbara.Platform.Storage.Postgres/Migrations/`](../../../src/Verbara.Platform.Storage.Postgres/Migrations/) (last file is `024_EncryptOidcClientSecret.sql`, dated 2026-05-18). The `cluster_distributed_lock` table is owned by the NEW SDK package `Verbara.Sdk.Cluster.Postgres` (migration `V001__DistributedLockSchema.sql`), invoked by the hotfix.
- **NOT Plan B.** This plan lands the binaries + topology. The functional smoke test (4-replica leader election + zero-duplicate SignalR delivery) lives in [`docs/plans/active/2026-05-23-phase-a5-talos-smoke-test.md`](2026-05-23-phase-a5-talos-smoke-test.md) (a sibling plan) and runs immediately after this one passes.

## 2. Current state inventory (lab, confirmed 2026-05-23)

### 2.1 Helm release

| Item | Value |
|---|---|
| Release name | `platform` |
| Release namespace | `default` *(historical — chart templates render INTO `r55-platform`; `helm list -A` shows the release object in `default`)* |
| Chart | `platform-0.2.1` |
| App version | `2.3.1` |
| Revision | `15` (deployed 2026-05-18 16:18:38) |
| Last upgrade status | `deployed` (revision 6 failed with `context deadline exceeded` on 2026-05-16; 7-15 all succeeded) |
| User-supplied values | `api.image.repository=192.168.122.1:5050/verbara-platform/api`, `web.image.repository=192.168.122.1:5050/verbara-platform/web` |

### 2.2 Workloads (in `r55-platform`)

| Resource | Count / value |
|---|---|
| `deployment/platform-api` | 2/2 Ready, image `192.168.122.1:5050/verbara-platform/api@sha256:e0c876329fbeb24cfa1fd2c3a14da905ba45953ea12e19f8756b03b313fe0a8d` — matches `v2.3.1` per [`authorized-digests.json`](../../../../verbara-website/data/authorized-digests.json). |
| `deployment/web` | 1/1 Ready, image `192.168.122.1:5050/verbara-platform/web:v3.1.2-web`. |
| `deployment/platform-realtime` | **DOES NOT EXIST** — chart's realtime templates not yet rendered into this revision (chart 0.2.1 in lab predates the chart-HEAD additions). |
| `deployment/platform-renderer` | DOES NOT EXIST — never deployed (chart never templated; service runs only in the canonical docker-compose stacks today). |
| `deployment/platform-mail` | DOES NOT EXIST — same. |
| `service/platform-api` | 5000/TCP, ClusterIP 10.107.17.168. |
| `service/web` | 80/TCP, ClusterIP 10.98.198.196. |
| `hpa/platform-api` | min=2 / max=8 / target=70% CPU (idle: CPU unknown — `metrics-server` may need a restart; not blocking). |
| Secrets | `jwt-signing-key` (RSA private key, ADR-0012 pre-rotation-pool baseline), `platform-pg-credentials` (basic-auth user/password for postgres user `platform`), `verbara-lab-license` (4-day-old `.lic`, 534 B). |
| NetworkPolicies | `default-deny-ingress` + `allow-api-ingress` + `allow-web-ingress` + `allow-prometheus-scrapes` + `allow-blackbox-from-monitoring`. |
| Ingress | None in `r55-platform` (HTTPRoute lives in the `default` namespace as `cilium-gateway-platform-gateway` LoadBalancer service 192.168.122.192). |

### 2.3 Data plane (in `r55-data`)

| Resource | Status |
|---|---|
| CNPG `cluster/postgres` | 3 instances Ready; primary `postgres-2`; `Cluster in healthy state` (18d uptime). |
| `redis-0` | 1/1 Ready. |
| `postgres-pooler` | 2/2 Ready (PgBouncer transactional pooler). |

**UNKNOWN — needs maintainer to execute one read query** (subagent's `kubectl exec` against the shared CNPG was denied by auto-mode classifier):

```sh
kubectl -n r55-data exec postgres-2 -c postgres -- \
  psql -U postgres -d verbara -c "SELECT name FROM _migrations ORDER BY name;"
```

The maintainer MUST run this once before C.3 and paste the result into `docs/operations/lab-v2.3.1-baseline-inventory-2026-05-23.txt` (C.2). The expected list is `001_InitialSchema.sql` → `024_EncryptOidcClientSecret.sql` — 24 files. If the live state diverges (e.g. earlier than 024, or an out-of-band migration is missing), C.3 needs an extra step before C.4.

### 2.4 Other namespaces (untouched)

`r55-asterisk` (PBX), `chaos-mesh`, `cnpg-system`, `cilium-secrets`, `kube-system`, `local-path-storage`, `monitoring` (prometheus + loki), `default` (Cilium Gateway + helm release object) — none are in scope.

### 2.5 Local registry

Hosted at `192.168.122.1:5050` (insecure HTTP, behind the KVM host). Confirmed images:

- `verbara-platform/api:v2.3.1` (digest matches deployed pods).
- `verbara-platform/web:v3.1.2-web`.
- **Missing:** `verbara-platform/realtime:*`, `renderer:*`, `mail:*` at ANY tag — never pushed.

### 2.6 ghcr.io (upstream)

| Image | v2.4.2 manifest-list digest (current) |
|---|---|
| `ghcr.io/verbara/platform/api:v2.4.2` | `sha256:bb5e90123c6520dd881d8a732b4af1959e86af76193ad395a70d0031f7784efb` — already in [`authorized-digests.json`](../../../../verbara-website/data/authorized-digests.json) `current[0]` via commit `01c455f` (HEAD on main, NOT YET PUSHED). |
| `ghcr.io/verbara/platform/realtime:v2.4.2` | Built + signed + pushed earlier this session per ADR-0023. Not tracked in `authorized-digests.json` (file currently only tracks `api` per its schema). |
| `ghcr.io/verbara/platform/renderer:v2.4.2` | Same. |
| `ghcr.io/verbara/platform/mail:v2.4.2` | Same. |
| All four | cosign-signed; verifiable with the public key at [`infra/k8s/helm/platform/files/cosign.pub`](../../../infra/k8s/helm/platform/files/cosign.pub). |

`v2.4.3` does NOT exist yet on ghcr.io. Building it is part of step C.0 below.

### 2.7 Pending workspace state (un-pushed)

| Repo | Branch | HEAD | Status |
|---|---|---|---|
| `Verbara.Platform` | `main` | `fe8a1938` (`release: Platform v2.4.2 — Realtime leader-gate (ADR-0022 Phase A.5 closure)`) | Tagged locally as `v2.4.1` is the latest pushed tag; `v2.4.2` is NOT YET TAGGED (`Directory.Build.props` reads `2.4.2`, but `git tag --sort=-creatordate` tops at `v2.4.1`). NOT YET PUSHED to remote. |
| `verbara-website` | `main` | `01c455f` (`feat(digests): authorize Verbara.Platform v2.4.2 (ADR-0022 Phase A.5 closure)`) | NOT YET PUSHED — this commit will be **amended** (not added-on) by step C.0.5 to retarget v2.4.3 instead of v2.4.2. |

## 3. Target state inventory (post-migration, end of C.6)

### 3.1 Helm release

| Item | Value |
|---|---|
| Release name | `platform` (same — upgrade in place) |
| Release namespace | `default` (unchanged — adoption already happened ages ago) |
| Chart | `platform-0.2.2` (Chart.yaml `version` bump as part of v2.4.3 release) |
| App version | `2.4.3` |
| Revision | `16` (next after current 15) |
| User-supplied values | `api.image.repository=192.168.122.1:5050/verbara-platform/api`, `web.image.repository=192.168.122.1:5050/verbara-platform/web`, `realtime.image.repository=192.168.122.1:5050/verbara-platform/realtime`, `renderer.image.repository=192.168.122.1:5050/verbara-platform/renderer`, `mail.image.repository=192.168.122.1:5050/verbara-platform/mail`, **all image tags `v2.4.3`**. |

### 3.2 Workloads (in `r55-platform`)

| Resource | Target |
|---|---|
| `deployment/platform-api` | 2/2 Ready, image `192.168.122.1:5050/verbara-platform/api@<v2.4.3 digest>` (Native AOT binary). HPA min=2 / max=8 unchanged. |
| `deployment/platform-realtime` | **NEW** — 4/4 Ready (HPA min=1 / max=4 per [`values.yaml:160-169`](../../../infra/k8s/helm/platform/values.yaml)). 4 replicas reached by HPA scale-up trigger OR by initial replicas override; lab default is `realtime.replicas=1` so the smoke test must `--set realtime.replicas=4` to materialize the multi-pod gate before HPA acts. |
| `deployment/platform-renderer` | NEW — 1/1 Ready (chart adds renderer + mail templates as part of ADR-0023 ship; if not already in chart HEAD, add them in the v2.4.3 release commit alongside Chart.yaml bump). |
| `deployment/platform-mail` | NEW — 1/1 Ready (same). |
| `deployment/web` | unchanged: 1/1 Ready at `v3.1.2-web`. |
| `cluster_distributed_lock` table | **NEW** in CNPG `verbara` DB, single row keyed `realtime:fanout:leader`, owner = one of the 4 realtime pod names, `expires_at` 10-30 s in the future. Created by the v2.4.3 hotfix's startup migration call (NOT pre-applied manually). |
| Network policies | Unchanged + 1 new `allow-realtime-ingress` (chart already templates this if `realtime.ingress.enabled=true`; if not, document the gap). |

### 3.3 ghcr.io (post-cutover)

| Image | v2.4.3 |
|---|---|
| `ghcr.io/verbara/platform/api:v2.4.3` | **Re-tagged from v2.4.2** via `docker buildx imagetools create` — Api binary is BYTE-IDENTICAL because the only source change is in `Verbara.Platform.Realtime`. Manifest-list digest identical to v2.4.2 (`sha256:bb5e90...`). |
| `ghcr.io/verbara/platform/realtime:v2.4.3` | **Rebuilt fresh** with the EnsureSchemaAsync hotfix. New manifest-list digest (compute during C.0.3). |
| `ghcr.io/verbara/platform/renderer:v2.4.3` | Re-tagged from v2.4.2. Byte-identical. |
| `ghcr.io/verbara/platform/mail:v2.4.3` | Re-tagged from v2.4.2. Byte-identical. |
| All four | cosign-signed at the new `:v2.4.3` reference using the existing maintainer cosign keypair via `~/.verbara/secrets.env`. |

### 3.4 verbara-website

| File | State |
|---|---|
| [`data/authorized-digests.json`](../../../../verbara-website/data/authorized-digests.json) | Amended commit `01c455f` retargeted from v2.4.2 → v2.4.3: `current[0].platform_version = "v2.4.2"` stays at `2.4.2` digest (Api unchanged → digest unchanged), or **a new `current[0]` is added for `v2.4.3` with the SAME digest as v2.4.2** (matches "Api binary is byte-identical" reality), and the previous v2.4.2 entry slides into `deprecated[]`. Maintainer decision in C.0.5 — both are defensible (see §8 Open Question 3). |

## 4. The v2.4.3 hotfix (single-file source change)

### 4.1 Scope

ONE file edited in [`src/Verbara.Platform.Realtime/Program.cs`](../../../src/Verbara.Platform.Realtime/Program.cs):

Insert a startup-time invocation of [`Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync`](../../../../Verbara.Sdk/src/Verbara.Sdk.Cluster.Postgres/Migrations/MigrationRunner.cs) right **after** `var app = builder.Build();` and **before** any `app.Use*()` / `app.Map*()` middleware wiring.

### 4.2 Exact insertion point

In the current `Program.cs` (commit `fe8a1938`, lines ~225-240), the sequence is:

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RealtimeContractsJsonContext.Default);
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoint();
app.MapHub<PlatformHub>("/hubs/platform");

await app.RunAsync();
```

The hotfix inserts a `using` scope between `var app = builder.Build();` and `app.UseAuthentication();`:

```csharp
var app = builder.Build();

// ─── Phase A.5 Gap-1 fix (v2.4.3): ensure cluster_distributed_lock table ─────
// Pro.Cluster's AddVerbaraCluster + AddPostgresDistributedLock register DI for
// the lock primitive but DO NOT auto-create the schema. The SDK ships
// MigrationRunner.EnsureSchemaAsync as an explicit opt-in (every other Pro
// storage package follows the same "host invokes migration at startup"
// pattern). Without this call the leader-election TryAcquireAsync fails on
// first request with relation "cluster_distributed_lock" does not exist and
// the pod restart-loops. Validated via the docker-compose smoke test that
// pre-seeded the table via postgres-init.sql (Gap-1 in
// docs/operations/phase-a5-smoke-test-2026-05-23.md §5).
using (var migrationScope = app.Services.CreateScope())
{
    var migrationDataSource = migrationScope.ServiceProvider
        .GetRequiredKeyedService<NpgsqlDataSource>("Cluster");
    var migrationLogger = migrationScope.ServiceProvider
        .GetRequiredService<ILogger<Program>>();
    await Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync(
        migrationDataSource,
        migrationLogger,
        app.Lifetime.ApplicationStopping);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoint();
app.MapHub<PlatformHub>("/hubs/platform");

await app.RunAsync();
```

### 4.3 Versioning changes (alongside the source change)

| File | Edit |
|---|---|
| [`Directory.Build.props`](../../../Directory.Build.props) `<PackageVersion>` | `2.4.2` → `2.4.3` |
| [`infra/k8s/helm/platform/Chart.yaml`](../../../infra/k8s/helm/platform/Chart.yaml) `version` | `0.2.1` → `0.2.2` |
| [`infra/k8s/helm/platform/Chart.yaml`](../../../infra/k8s/helm/platform/Chart.yaml) `appVersion` | `"2.3.1"` → `"2.4.3"` |
| [`infra/k8s/helm/platform/values.yaml`](../../../infra/k8s/helm/platform/values.yaml) `api.image.tag` | `"v2.3.1"` → `"v2.4.3"` (production default — lab override remains in user-supplied values) |
| [`infra/k8s/helm/platform/values.yaml`](../../../infra/k8s/helm/platform/values.yaml) `api.image.digest` | `"sha256:e0c876329fbeb24cfa1fd2c3a14da905ba45953ea12e19f8756b03b313fe0a8d"` → `"sha256:bb5e90123c6520dd881d8a732b4af1959e86af76193ad395a70d0031f7784efb"` (same v2.4.2 digest IF maintainer chooses the "Api binary byte-identical → reuse digest" path in C.0.5 Open Question 3; otherwise compute the new digest). |
| [`infra/k8s/helm/platform/values.yaml`](../../../infra/k8s/helm/platform/values.yaml) `realtime.image.tag` | `"v0.1.0-rc"` → `"v2.4.3"` |
| [`infra/k8s/helm/platform/values.yaml`](../../../infra/k8s/helm/platform/values.yaml) `realtime.image.digest` | `""` → `"sha256:<computed in C.0.3>"` |
| Chart `Renderer`/`Mail` templates | If not yet templated under [`infra/k8s/helm/platform/templates/`](../../../infra/k8s/helm/platform/templates/), add them in the v2.4.3 release commit (mirror `realtime-*.yaml` topology). If already there post-fe8a1938 (verify with `ls`), skip. |
| Release commit message | `release: Platform v2.4.3 — Realtime startup migration hotfix (ADR-0022 Phase A.5 Gap-1)` |

### 4.4 Tests

The existing 4 `PushToHubRelayTests` from commit `fe8a1938` already validate the leader-gate behavior with a `FakeClusterLeader`. ADD one new integration test in `tests/Verbara.Platform.Realtime.Tests/Migrations/RealtimeStartupMigrationTests.cs`:

```csharp
EnsureSchemaAsync_ShouldCreateClusterDistributedLockTable_WhenInvokedAgainstFreshPostgres
```

The test:
1. Spins a Testcontainers Postgres 18 (or reuses the existing PG fixture infrastructure from `Verbara.Sdk.Cluster.Postgres.Tests`).
2. Runs the same `EnsureSchemaAsync` call the host makes.
3. Asserts `SELECT to_regclass('cluster_distributed_lock')` returns the OID.
4. Asserts a second invocation is idempotent (no exception, no duplicate rows).

This earns its space — without it the hotfix has zero direct test coverage; the existing tests pre-seed the table via fixture init.

### 4.5 Build / pack / push / sign sequence (mirrors C.6 of [Phase A.5 plan](2026-05-22-phase-a5-cluster-leader-election.md))

All steps **maintainer-executed** (subagent describes only):

```bash
# 1. Source signing secrets (cosign key + ghcr.io token)
source ~/.verbara/secrets.env   # exports COSIGN_PASSWORD, COSIGN_KEY_PATH, GHCR_USER, GHCR_TOKEN

# 2. Verify the source edit + version bumps committed
cd /media/Data/Source/Verbara/Verbara.Platform
git status   # expect: Program.cs + Directory.Build.props + Chart.yaml + values.yaml + new test
git diff --cached   # review
# Commit the source change:
#   git commit -m "fix(realtime): invoke MigrationRunner.EnsureSchemaAsync at startup (ADR-0022 Phase A.5 Gap-1)"
# Commit the release bumps as a separate commit:
#   git commit -m "release: Platform v2.4.3 — Realtime startup migration hotfix (ADR-0022 Phase A.5 Gap-1)"

# 3. Build ONLY the Realtime image (the other 3 are byte-identical)
docker build \
  -f src/Verbara.Platform.Realtime/Dockerfile.realtime \
  -t ghcr.io/verbara/platform/realtime:v2.4.3 \
  --build-arg GHCR_USER=$GHCR_USER \
  --build-arg GHCR_TOKEN=$GHCR_TOKEN \
  .

# 4. Re-tag the other 3 v2.4.2 images as v2.4.3 (zero-byte upload thanks to layer reuse)
for svc in api renderer mail; do
  docker buildx imagetools create \
    --tag ghcr.io/verbara/platform/$svc:v2.4.3 \
    ghcr.io/verbara/platform/$svc:v2.4.2
done

# 5. Push the freshly built realtime image
docker push ghcr.io/verbara/platform/realtime:v2.4.3

# 6. Cosign-sign all 4 v2.4.3 tags
for svc in api realtime renderer mail; do
  cosign sign --yes \
    --key $COSIGN_KEY_PATH \
    ghcr.io/verbara/platform/$svc:v2.4.3
done

# 7. Verify the 4 signatures
for svc in api realtime renderer mail; do
  cosign verify \
    --key infra/k8s/helm/platform/files/cosign.pub \
    ghcr.io/verbara/platform/$svc:v2.4.3
done

# 8. Capture the manifest-list digests for chart wiring + website authorization
for svc in api realtime renderer mail; do
  echo "$svc:"
  docker buildx imagetools inspect ghcr.io/verbara/platform/$svc:v2.4.3 \
    --format '{{json .Manifest.Digest}}'
done | tee /tmp/v2.4.3-digests.txt
```

Expected outcome: `/tmp/v2.4.3-digests.txt` lists 4 digests. The `api` digest matches `v2.4.2` exactly (byte-identical). The `realtime` digest is new. The `renderer` + `mail` digests match `v2.4.2` exactly.

### 4.6 Amend verbara-website commit `01c455f`

The pending un-pushed commit currently authorizes `v2.4.2` only. Two equally-valid retarget strategies:

**Option A — Amend in-place, retarget to v2.4.3 with same digest**:

```bash
cd /media/Data/Source/Verbara/verbara-website
# Edit data/authorized-digests.json:
#   current[0].platform_version: "v2.4.2" → "v2.4.3"
#   current[0].image_ref: ".../api:v2.4.2" → ".../api:v2.4.3"
#   current[0].manifest_list_digest: SAME (Api is byte-identical)
#   current[0].released_at: bump timestamp
#   deprecated[0]: add v2.4.2 entry with same digest, so the v2.4.2 tag remains a valid alias
git add data/authorized-digests.json
git commit --amend --no-edit
# Commit message stays "feat(digests): authorize Verbara.Platform v2.4.3 ..."
# (manually edit message: s/v2.4.2/v2.4.3/g)
```

**Option B — Add a NEW commit on top of `01c455f`** that promotes v2.4.3 into `current[0]` and demotes v2.4.2 into `deprecated[]`. Preserves audit trail (v2.4.2 was a real intermediate ship). The maintainer commit log clearly shows both versions existed.

**Recommended:** **Option B**, because the Phase A.5 closure narrative is genuinely two-step (v2.4.2 shipped but missing the migration call; v2.4.3 hotfixed it). Option A rewrites history to make v2.4.2 disappear, which is less honest. See §8 Open Question 3 for the maintainer's final call.

## 5. Migration phases (ordered, with rollback gates)

Each phase has a single explicit pass/fail gate before the next phase starts. Total estimated wall-clock: 60-90 minutes if everything works; 2-3 hours including the smoke test in Plan B.

### C.0 — Pre-flight: ship v2.4.3 to ghcr.io (§4.5 + §4.6) — 30-45 min

Already detailed above. Pass gate: `cosign verify` returns success for all 4 v2.4.3 tags AND `/tmp/v2.4.3-digests.txt` is captured.

### C.1 — Mirror v2.4.3 images to the lab registry — 5-10 min

The lab cluster's worker nodes pull from `192.168.122.1:5050` (insecure HTTP, no auth, no outbound internet) — NOT from ghcr.io. Use `imagetools create` to retag-mirror the just-signed ghcr.io tags:

```bash
for svc in api realtime renderer mail; do
  docker buildx imagetools create \
    --tag 192.168.122.1:5050/verbara-platform/$svc:v2.4.3 \
    ghcr.io/verbara/platform/$svc:v2.4.3
done
```

**Pass gate:**
```bash
for svc in api realtime renderer mail; do
  curl -s http://192.168.122.1:5050/v2/verbara-platform/$svc/tags/list | jq .
done
# Expect: each response includes "v2.4.3" in the tags array.
```

**Rollback:** none needed — adding tags is non-destructive.

**Note on registry mirror integrity:** `imagetools create` copies the **manifest** but the local registry's blob store still needs to ingest the layers. The HTTPS-less local registry at `:5050` MUST accept the push (no auth, but watch for "blob upload unknown" errors if registry storage is full). If push fails: `df -h /var/lib/registry` on the KVM host, prune old image versions if needed.

### C.2 — Capture v2.3.1 baseline inventory for rollback — 10 min

One-time snapshot of the current state, persisted in the repo as a rollback reference:

```bash
mkdir -p docs/operations/
{
  echo "# Lab v2.3.1 baseline inventory snapshot — 2026-05-23"
  echo ""
  echo "## helm release"
  helm -n default get manifest platform
  echo ""
  echo "## kubectl resources (r55-platform)"
  kubectl -n r55-platform get all -o yaml
  echo ""
  echo "## kubectl resources (r55-platform): configmap + secret names only"
  kubectl -n r55-platform get configmap,secret -o name
  echo ""
  echo "## CNPG migration state"
  kubectl -n r55-data exec postgres-2 -c postgres -- \
    psql -U postgres -d verbara -c "SELECT name, applied_at FROM _migrations ORDER BY name;"
  echo ""
  echo "## helm history"
  helm -n default history platform
} > docs/operations/lab-v2.3.1-baseline-inventory-2026-05-23.txt
```

**Pass gate:** file exists and contains all four sections non-empty.

**Rollback:** none — read-only operation.

### C.3 — CNPG migration window — 5 min

The Storage.Postgres migrations between v2.3.1 and v2.4.3 are **EMPTY** (last migration `024_EncryptOidcClientSecret.sql` shipped pre-v2.3.1; verified via `git log v2.3.1..HEAD -- src/Verbara.Platform.Storage.Postgres/Migrations/` returns 0 commits). The new Realtime startup-migration call (§4.2) creates ONE new table (`cluster_distributed_lock`) idempotently on first pod start.

**Strategy:** let the Realtime pod's `EnsureSchemaAsync` call run the migration at startup. No manual pre-application needed.

**Pre-flight safety check:** confirm the maintainer's CNPG `_migrations` table is in the expected v2.3.1 state. The maintainer runs:

```bash
kubectl -n r55-data exec postgres-2 -c postgres -- \
  psql -U postgres -d verbara -c "
    SELECT name FROM _migrations WHERE name LIKE '024%';
    SELECT to_regclass('cluster_distributed_lock');
  "
# Expected: 024_EncryptOidcClientSecret.sql present, cluster_distributed_lock = NULL.
```

If `_migrations` is missing `024`: STOP — the lab is behind the codebase's pre-v2.3.1 baseline; investigate before continuing.

If `cluster_distributed_lock` already exists: the upgrade is a no-op for that table; the idempotent migration call will succeed.

**CNPG backup snapshot before C.5:**

```bash
kubectl -n r55-data create -f - <<EOF
apiVersion: postgresql.cnpg.io/v1
kind: Backup
metadata:
  name: pre-v2.4.3-migration-$(date +%s)
  namespace: r55-data
spec:
  cluster:
    name: postgres
  method: barmanObjectStore
EOF
# Wait for status.phase == "completed":
kubectl -n r55-data wait --for=jsonpath='{.status.phase}'=completed backup/pre-v2.4.3-migration-* --timeout=10m
```

If barman object store is not configured (lab may use volume snapshots instead — check `kubectl -n r55-data get cluster postgres -o yaml | grep -A 10 backup`), fall back to:

```bash
kubectl -n r55-data exec postgres-2 -c postgres -- \
  pg_dump -U postgres verbara > /tmp/verbara-pre-v2.4.3.sql
```

**Pass gate:** backup completed OR pg_dump produced a non-empty `/tmp/verbara-pre-v2.4.3.sql`.

### C.4 — Helm adoption / chart-history decision — 5 min

**Already resolved:** the chart at HEAD is in maintained-helm-release shape (revision 15 `deployed`). NO adoption needed. The chart already manages `platform-api`, `web`, NetworkPolicies, and Services in `r55-platform`.

The previously-feared "raw kubectl/kustomize → helm adoption" risk does NOT apply here — `helm -n default get manifest platform` produces the current state correctly.

**Pass gate:** `helm -n default status platform` reports `STATUS: deployed`. No special adoption command is required.

**If adoption WERE required** (hypothetical, for documentation): the canonical `kubectl annotate ... meta.helm.sh/release-name=platform meta.helm.sh/release-namespace=default` + `kubectl label app.kubernetes.io/managed-by=Helm` per [helm-mapkubeapis convention](https://github.com/helm/helm/issues/7649) would be the path. Not needed.

### C.5 — `helm upgrade --install platform` — 5-10 min

```bash
cd /media/Data/Source/Verbara/Verbara.Platform

helm upgrade --install platform infra/k8s/helm/platform \
  -n default \
  --reuse-values \
  --set api.image.repository=192.168.122.1:5050/verbara-platform/api \
  --set api.image.tag=v2.4.3 \
  --set api.image.digest=sha256:<api-v2.4.3-digest-from-C.0.7> \
  --set realtime.image.repository=192.168.122.1:5050/verbara-platform/realtime \
  --set realtime.image.tag=v2.4.3 \
  --set realtime.image.digest=sha256:<realtime-v2.4.3-digest-from-C.0.7> \
  --set realtime.replicas=4 \
  --set web.image.repository=192.168.122.1:5050/verbara-platform/web \
  --set web.image.tag=v3.1.2-web \
  --wait \
  --timeout 10m
```

**Notes on the command:**
- `-n default` matches the release's actual namespace (NOT `r55-platform` — that's the workload namespace).
- `--reuse-values` preserves the historical `api.image.repository` + `web.image.repository` lab overrides while only changing image tags.
- `--set realtime.replicas=4` forces the multi-pod target immediately rather than waiting for HPA scale-up. The chart's HPA still owns the dynamic adjustment between 1 and 4 post-deploy; the `--set` only seeds the initial replica count.
- `digest` overrides are critical for Pro/ADR-0011 image-binding to validate against the v2.4.3 entry in `authorized-digests.json`. Without them the pods report 12002 startup warning but continue.
- `--wait` blocks until all Deployments report `Available=True`. 10 min ceiling handles slow first-time image pulls into the local registry blob cache.

**Pass gate:**
```bash
helm -n default status platform | grep "STATUS: deployed"
kubectl -n r55-platform get deploy -l app.kubernetes.io/instance=platform \
  -o jsonpath='{range .items[*]}{.metadata.name}{": "}{.status.availableReplicas}/{.status.replicas}{"\n"}{end}'
# Expected:
#   platform-api: 2/2
#   platform-realtime: 4/4
#   platform-renderer: 1/1   (if chart templates renderer)
#   platform-mail: 1/1       (if chart templates mail)
#   web: 1/1
```

**Rollback:**
```bash
helm -n default rollback platform 15   # revert to v2.3.1 revision
# If the CNPG migration already ran (cluster_distributed_lock table exists),
# the rollback is forward-compatible — v2.3.1 doesn't read or write that table.
# No data restore needed.
```

### C.6 — Verify: leader election working, gap-1 closed — 10 min

```bash
# 1. All 4 realtime pods Ready
kubectl -n r55-platform get pods -l app.kubernetes.io/name=platform-realtime

# 2. cluster_distributed_lock row exists, exactly one
kubectl -n r55-data exec postgres-2 -c postgres -- \
  psql -U postgres -d verbara -c "
    SELECT resource, owner, expires_at, expires_at - NOW() AS ttl_remaining
    FROM cluster_distributed_lock
    WHERE resource = 'realtime:fanout:leader';
  "
# Expected: 1 row, owner = one of the 4 pod names from step 1, ttl_remaining > 0

# 3. The startup migration log line fired
kubectl -n r55-platform logs deploy/platform-realtime --tail=200 \
  | grep -E "(EnsureSchemaAsync|cluster_distributed_lock|Leadership transition)"
# Expected: at least one "Leadership transition" line; no "relation does not exist" errors

# 4. Healthcheck on every pod
for pod in $(kubectl -n r55-platform get pods -l app.kubernetes.io/name=platform-realtime -o name); do
  kubectl -n r55-platform exec $pod -- wget -qO- http://127.0.0.1:5030/health
done
# Expected: 4 × "Healthy" responses

# 5. API healthcheck (sanity — should be unaffected)
kubectl -n r55-platform exec deploy/platform-api -- wget -qO- http://127.0.0.1:5000/healthz
```

**Pass gate:** all 5 commands succeed; cluster_distributed_lock has 1 row; 0 occurrences of "relation does not exist" in any realtime pod's logs.

**Failure modes & responses:**
- *Realtime pods CrashLoopBackOff with "relation does not exist"*: the hotfix did not ship in the rebuilt image. Re-verify §4.2 made it into the v2.4.3 realtime image (`docker pull ghcr.io/verbara/platform/realtime:v2.4.3 && docker image inspect ... --format '{{.Created}}'` should be after the source edit timestamp).
- *Realtime pods CrashLoopBackOff with "connection refused"*: `ConnectionStrings__Postgres` not set. Verify the Helm chart's downward-API env wiring (commit `91e9e6cc` added `ConnectionStrings__Cluster` with `optional: true` fallback to `:Postgres`).
- *Multiple `cluster_distributed_lock` rows*: NOT possible — the table has `resource` as PRIMARY KEY. If somehow observed: Postgres data corruption; restore from C.3 backup.
- *Zero `cluster_distributed_lock` rows*: realtime pods haven't completed first renewal cycle (10 s default). Wait 15 s, re-query.

### C.7 — Hand off to Plan B (smoke test) — 0 min

After C.6 PASS, this plan moves to `docs/plans/completed/` and the maintainer kicks off [`docs/plans/active/2026-05-23-phase-a5-talos-smoke-test.md`](2026-05-23-phase-a5-talos-smoke-test.md) (Plan B), which performs the leader-failover + zero-duplicate-SignalR-delivery validation per §6 of the [Phase A.5 plan](2026-05-22-phase-a5-cluster-leader-election.md).

## 6. Rollback procedure

### 6.1 In-flight rollback (during C.5 helm upgrade)

`helm upgrade --wait` will surface failures within the 10-min ceiling. If the upgrade fails:

```bash
helm -n default status platform   # confirm STATUS: failed
helm -n default rollback platform 15   # revert to v2.3.1 revision
kubectl -n r55-platform get pods -w   # watch revert
```

The previous v2.3.1 ReplicaSets are still in `kubectl -n r55-platform get rs` (helm leaves them) and the rollback re-scales them to desired count.

### 6.2 Post-deploy rollback (C.6 fails)

Same `helm -n default rollback platform 15`. The `cluster_distributed_lock` table created by the hotfix's startup migration call is **forward-compatible** with v2.3.1 (v2.3.1 doesn't reference it). No DROP TABLE needed. The table will sit unused until the next upgrade attempt.

### 6.3 CNPG data corruption (extreme case — should not happen)

Restore from the C.3 backup:

```bash
kubectl -n r55-data exec postgres-2 -c postgres -- \
  psql -U postgres -d verbara < /tmp/verbara-pre-v2.4.3.sql
```

OR (if barman backup):

```bash
kubectl -n r55-data create -f - <<EOF
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata:
  name: postgres-restored
  namespace: r55-data
spec:
  bootstrap:
    recovery:
      backup:
        name: pre-v2.4.3-migration-<timestamp>
EOF
```

### 6.4 Image rollback (registry pollution)

`v2.4.3` tags can be deleted from the local registry without affecting v2.4.2/v2.3.1:

```bash
# ghcr.io: use gh CLI
for svc in api realtime renderer mail; do
  gh api -X DELETE /user/packages/container/platform%2F$svc/versions/<version-id-for-v2.4.3>
done
# Local registry: depends on storage driver; typically:
curl -X DELETE http://192.168.122.1:5050/v2/verbara-platform/realtime/manifests/<digest>
```

## 7. Risks

| # | Risk | Probability | Impact | Mitigation |
|---|------|-------------|--------|------------|
| R1 | `imagetools create` retag fails for byte-identical images (`api`, `renderer`, `mail`) due to manifest-list cross-platform variance (the v2.4.2 manifest lists may include `linux/arm64` slices the local registry doesn't accept). | Low | Med — would force per-arch rebuilds | Pre-check: `docker buildx imagetools inspect ghcr.io/verbara/platform/api:v2.4.2 --raw`. If multi-arch: pin to `--platform linux/amd64` during the retag (the lab only runs amd64). |
| R2 | Helm adoption vs install-fresh trade-off — already resolved (release was helm-managed since at least revision 6). | None (resolved) | n/a | n/a |
| R3 | CNPG migration window — the hotfix's `EnsureSchemaAsync` runs on EVERY realtime pod start, so 4 pods race for the `CREATE TABLE IF NOT EXISTS` advisory lock. | Low | Low — `IF NOT EXISTS` is concurrency-safe in Postgres | Documented as "expected behavior"; `EnsureSchemaAsync` is built for this race. |
| R4 | Webhook secret rotation if any v2.4.x ship rotated `serviceKey` or `jwtSigningKey`. | Low | Med — agents would stop accepting tokens mid-deploy | Verify before C.5: `kubectl -n r55-platform get secret jwt-signing-key -o jsonpath='{.metadata.creationTimestamp}'` and compare with the timestamp of any in-repo rotation commit since v2.3.1. The lab secret is 18d old (matches v2.3.1 era) — should NOT have rotated. If maintainer rotated out-of-band: re-create secret BEFORE C.5. |
| R5 | Realtime pod uses `Cluster__InstanceId` = `$(POD_NAME)` via downward API but the chart's `optional: true` Secret fallback for `ConnectionStrings__Cluster` quietly falls through to `:Postgres` — if neither is set, leader-election crashes the pod. | Low | High — pod CrashLoopBackOff | Verify chart renders `ConnectionStrings__Postgres` env var (it does — chart commit `5ce89769`). |
| R6 | The cosign keypair at `~/.verbara/secrets.env` rotated between v2.4.1 and v2.4.3. Cluster Kyverno policy (chart's `imageVerification.enabled`) would reject the new v2.4.3 images. | Very Low | Med — pods stuck Pending until policy is updated | Chart default for `imageVerification.enabled` is `false`; lab CONFIRMED `false` (no Kyverno deployed). Even if it were enabled, the same key signed v2.4.1 and v2.4.2 successfully; rotation would have been an out-of-band event the maintainer would know about. |
| R7 | Lab registry blob storage full — push of new realtime image fails. | Low | Med | Pre-check: `ssh kvm-host df -h /var/lib/registry`. Prune via `registry garbage-collect /etc/docker/registry/config.yml` if needed. |
| R8 | The maintainer-pending `verbara-website` commit `01c455f` is FORCE-PUSHED to remote between this plan being drafted and step C.0.5 being executed (some other session pushed). | Very Low | Low — easy to re-amend | This plan's C.0.5 step is descriptive; before amending, the maintainer runs `git fetch origin main && git log origin/main..HEAD` to confirm `01c455f` is still un-pushed. |
| R9 | Plan B (smoke test) finds duplicate SignalR delivery despite leader-election running — meaning the leader gate has a latent bug not caught by the docker-compose smoke test or the unit tests. | Low | High — would invalidate Phase A.5 closure | Out of scope for this plan. Plan B owns this risk. If it triggers: open a follow-up Phase A.6 plan. |
| R10 | The chart at HEAD does NOT yet template `renderer` + `mail` Deployments (only `realtime` was added in commit `91e9e6cc`). | Medium | Low — non-blocking; lab functions without them | Step C.4 verification: `ls infra/k8s/helm/platform/templates/ | grep -E "renderer|mail"`. If absent: defer their deploy to a future chart bump OR add them in the v2.4.3 release commit. **Recommended:** defer; the lab does NOT need renderer/mail for the Phase A.5 smoke test (they only matter for PDF export + email send paths). |
| R11 | The release commit and the source-edit commit are squashed by the maintainer into one. | None | n/a | Cosmetic preference; either way is correct. Plan describes the two-commit pattern but a squash is equally valid. |
| R12 | The 4 realtime replicas schedule onto fewer than 4 nodes (the chart's `topologySpreadConstraints` is `whenUnsatisfiable: ScheduleAnyway` — soft). | Medium | Low | Lab has 4 nodes (cp1 + w1/w2/w3 — but cp1 is the control-plane and typically tainted). Realistic spread: 4 replicas onto 3 worker nodes. Two pods on one node is acceptable for the leader-election test (the lock owner can be either). |
| R13 | The `realtime.replicas=4` override survives the helm upgrade but the HPA immediately scales down to `minReplicas: 1` if the pods are idle. | Medium | Low | The smoke test (Plan B) issues load that pushes CPU above 70% so HPA settles at maxReplicas=4. For the migration validation alone (C.6), 1-or-more is acceptable. |

## 8. Open questions for the maintainer

These decisions MUST be answered before executing the corresponding step. Each one is yes/no or A/B.

1. **CNPG backup method for C.3:** barman object store (cloud), volume snapshot, or `pg_dump > /tmp/`? *Maintainer answer needed before C.3 backup step.* The lab uses Talos w/o cloud egress, so likely `pg_dump`. Verify with `kubectl -n r55-data get cluster postgres -o yaml | grep -A 20 backup`.

2. **Chart template additions for `renderer` + `mail` Deployments — defer or include in v2.4.3?** Plan currently RECOMMENDS DEFER (lab needs only Realtime for Phase A.5 closure). Maintainer can include them in the v2.4.3 release commit if desired for ADR-0023 completeness. *Maintainer answer needed before §4.3 release-commit content is finalized.*

3. **verbara-website amend strategy (§4.6):** Option A (amend in place — v2.4.2 disappears) or Option B (add new commit on top of `01c455f` — both versions preserved)? Plan RECOMMENDS Option B. *Maintainer answer needed before §4.6 is executed.*

4. **Tag `v2.4.2` retroactively?** The current state has `Directory.Build.props=2.4.2` and `commit fe8a1938` named "release: Platform v2.4.2 — …" but no `git tag v2.4.2` exists. If the maintainer wants a clean tag history (v2.4.1 → v2.4.2 → v2.4.3), tag `fe8a1938` as `v2.4.2` before tagging the v2.4.3 release commit. If the maintainer wants to absorb v2.4.2 into v2.4.3 (since v2.4.2 never went to ghcr.io for the migrated-table path), skip the v2.4.2 tag. *Maintainer answer needed before tagging in C.0.*

5. **Realtime initial replica count for the migration step:** `--set realtime.replicas=4` (force multi-pod immediately, simpler verification) or `realtime.replicas=1` + let HPA scale (more realistic production behavior)? Plan recommends `=4` because Plan B's leader-election test requires 4 pods regardless. *Maintainer answer needed before C.5 helm-upgrade command.*

6. **CNPG `_migrations` table read access:** subagent's `kubectl exec -n r55-data postgres-2 …` was denied by auto-mode (shared-DB read via remote shell). Maintainer runs the query manually for §2.3 — and confirms the actual list matches the expected `001_…` → `024_EncryptOidcClientSecret.sql` baseline. *Maintainer answer needed for §2.3 UNKNOWN.*

7. **`v3.1.2-web` upgrade as part of this train?** The web image is unchanged in the v2.4.x train (last bumped to `v3.1.2-web` in commit `74adc1a6` separately from Phase A.5). Plan RECOMMENDS hold at `v3.1.2-web`. *Maintainer answer needed before C.5.*

## 9. Acceptance criteria

All must be GREEN before this plan moves to `docs/plans/completed/`:

- [ ] **Source change committed:** `src/Verbara.Platform.Realtime/Program.cs` invokes `MigrationRunner.EnsureSchemaAsync` at startup; release commit message `release: Platform v2.4.3 — Realtime startup migration hotfix (ADR-0022 Phase A.5 Gap-1)`.
- [ ] **`Directory.Build.props`** reads `<PackageVersion>2.4.3</PackageVersion>`.
- [ ] **`Chart.yaml`** at `version: 0.2.2`, `appVersion: "2.4.3"`.
- [ ] **`values.yaml`** defaults updated: `api.image.tag=v2.4.3`, `realtime.image.tag=v2.4.3`, `realtime.image.digest=<computed>`.
- [ ] **New TDD test** `EnsureSchemaAsync_ShouldCreateClusterDistributedLockTable_WhenInvokedAgainstFreshPostgres` passes against Testcontainers Postgres 18.
- [ ] **AOT publish of `Verbara.Platform.Api`** is clean (0 IL2026 / IL3050 / IL207x diagnostics) — the v2.4.2 Native AOT property is preserved.
- [ ] **4 cosign-signed images** on ghcr.io at `v2.4.3`: api / realtime / renderer / mail. `cosign verify` returns success for each.
- [ ] **4 mirrored images** on `192.168.122.1:5050/verbara-platform/*:v2.4.3`. `curl /v2/.../tags/list` lists `v2.4.3` for each.
- [ ] **verbara-website** commit (amended or new — per Open Question 3) authorizes v2.4.3 digest. Pending un-pushed; will be pushed alongside Platform v2.4.3 tag.
- [ ] **Helm release upgraded:** `helm -n default status platform` reports `STATUS: deployed` at revision 16, chart `platform-0.2.2`, appVersion `2.4.3`.
- [ ] **Workloads Ready:** `platform-api` 2/2, `platform-realtime` 4/4 (or 1/1 if Open Question 5 chose HPA scale-up), `web` 1/1, plus renderer/mail if Open Question 2 included them.
- [ ] **`cluster_distributed_lock`** table exists in CNPG `verbara` DB, exactly one row keyed `realtime:fanout:leader`, `ttl_remaining > 0`.
- [ ] **No "relation does not exist" errors** in `kubectl -n r55-platform logs deploy/platform-realtime --tail=500`.
- [ ] **Healthcheck pass** on every realtime pod (`/health` → 200).
- [ ] **Baseline snapshot persisted:** `docs/operations/lab-v2.3.1-baseline-inventory-2026-05-23.txt` committed for rollback reference.
- [ ] **Plan B handed off:** [`2026-05-23-phase-a5-talos-smoke-test.md`](2026-05-23-phase-a5-talos-smoke-test.md) is ready to execute against the migrated lab.
- [ ] **This plan `git mv`** to `docs/plans/completed/`.

When all check-boxes are green AND Plan B passes, [`2026-05-22-phase-a5-cluster-leader-election.md`](2026-05-22-phase-a5-cluster-leader-election.md) §7 acceptance criteria are satisfied and that plan also moves to `completed/`, closing ADR-0022 Phase A.5 — the last open item on the ADR-0022 track. Memory updates:
- `project_current_position.md` — Phase A.5 CLOSED, ADR-0022 TRACK FULLY CLOSED, lab on v2.4.3.
- `project_roadmap.md` — strike Phase A.5 from open items.
