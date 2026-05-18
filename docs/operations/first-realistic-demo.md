# First realistic demo (~55 minutes)

A guided tour of the **R4 + R5 release train** features through a
realistic, multi-tenant contact-center scenario. After this you should be
able to demo the platform to a buyer or run an internal POC with
confidence.

> **Reading + execution time:** ~55 minutes (skip the optional AgentAssist
> section if you do not have a Deepgram or OpenAI key on hand and it cuts
> ~10 min).
> **Prerequisites:** [first-deploy.md](first-deploy.md) completed; you
> should have `MGMT_KEY`, `PLATFORM_JWT`, and `ACME_JWT` already in your
> shell. Re-run those exports if your shell session reset.

---

## Scenario

You are operating a managed-service contact center hosting three customer
tenants:

- **acme** (already created in first-deploy.md) — outbound sales focus.
- **globex** — inbound support focus, 8h SLA.
- **initech** — minimal demo tenant, used for cross-tenant isolation
  smoke checks.

You will:

1. Seed the two new tenants.
2. Send synthetic call traffic to populate the live wallboard + CDR.
3. Toggle AgentAssist at runtime (no restart).
4. Inspect the audit log, retention admin, license panel.
5. (Multi-node) Demo a graceful drain.
6. Walk a tenant-isolation smoke check (initech cannot see acme data).

Total: ~55 minutes.

---

## Step 1 — Seed two more tenants (5 min)

```bash
for t in globex initech; do
    curl -sf -X POST http://localhost:5000/api/v1/management/tenants \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d "{\"tenantId\":\"$t\",\"name\":\"${t^} Demo\",\"type\":2}"

    curl -sf -X PUT "http://localhost:5000/api/v1/management/tenants/$t/settings" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d '{"plan":"Pro"}'

    curl -sf -X POST http://localhost:5000/api/v1/admin/users \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $PLATFORM_JWT" \
        -H "X-Tenant-Id: $t" \
        -d "{\"userId\":\"$t-user-admin\",\"email\":\"admin@$t.local\",\"displayName\":\"${t^} Admin\",\"role\":\"Admin\",\"password\":\"${t^}Admin2026!\"}"
done
```

Confirm in the Web UI: Platform Admin → Tenants. You should see four rows:
`platform` (host), `acme`, `globex`, `initech`.

---

## Step 2 — Provision agents + queues for globex (5 min)

```bash
GLOBEX_JWT=$(curl -sf -X POST http://localhost:5000/api/v1/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"globex","email":"admin@globex.local","password":"GlobexAdmin2026!"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")

# Two support agents
for ext in 5001 5002; do
    UID="globex-user-$ext"
    curl -sf -X POST http://localhost:5000/api/v1/admin/users \
        -H "Content-Type: application/json" -H "Authorization: Bearer $GLOBEX_JWT" \
        -H "X-Tenant-Id: globex" \
        -d "{\"userId\":\"$UID\",\"email\":\"agent$ext@globex.local\",\"displayName\":\"Globex Agent $ext\",\"role\":\"Agent\",\"password\":\"GlobexAgent2026!\"}"

    AGENT=$(curl -sf -X POST http://localhost:5000/api/v1/admin/agents \
        -H "Content-Type: application/json" -H "Authorization: Bearer $GLOBEX_JWT" \
        -H "X-Tenant-Id: globex" \
        -d "{\"userId\":\"$UID\",\"displayName\":\"Globex $ext\",\"extension\":\"$ext\",\"sipPassword\":\"globex$ext\"}")
    AGENT_ID=$(echo "$AGENT" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])")

    # Skills are managed via PUT /admin/agents/{id} after create
    curl -sf -X PUT "http://localhost:5000/api/v1/admin/agents/$AGENT_ID" \
        -H "Content-Type: application/json" -H "Authorization: Bearer $GLOBEX_JWT" \
        -H "X-Tenant-Id: globex" \
        -d '{"skills":["support"]}'
done

# Support queue + add both agents
curl -sf -X POST http://localhost:5000/api/v1/admin/queues \
    -H "Content-Type: application/json" -H "Authorization: Bearer $GLOBEX_JWT" \
    -H "X-Tenant-Id: globex" \
    -d '{"name":"Globex Support","isActive":true}'
```

---

## Step 3 — Generate synthetic call traffic (10 min)

Drive the live wallboard + CDR by originating a burst of calls from the
Asterisk CLI. The dialplan will queue them through Acme Sales (4001) and
Globex Support (5001 / 5002).

```bash
# 10 calls over 2 minutes, randomly to acme or globex
for i in $(seq 1 10); do
    if (( i % 2 == 0 )); then
        EXT=4001
    else
        EXT=$((5000 + RANDOM % 2 + 1))
    fi
    docker compose -f docker/docker-compose.full.yml exec -T asterisk \
        asterisk -rx "channel originate Local/$EXT@default application Playback hello-world"
    sleep 12
done
```

Open **Web UI → Analytics → Live Wallboard** in two browser tabs (one
signed in to `acme`, one to `globex`). Watch the queue tiles update in
real time — you should see active call counters tick up + down, average
wait time emerge, abandoned calls (if you ignore some) tracked.

> The wallboard is wired to the **Pro.Analytics.Live** pipeline shipped in
> R5.1: `LiveQueueSnapshotWriter` coalesces ~5 Hz per `(tenantId, queueName)`
> and writes to the `live_queue_snapshots` table. The Web reads via
> `ILiveQueueMetricsProvider`.

---

## Step 4 — AgentAssist runtime toggle (~10 min, OPTIONAL)

> **External setup required.** This step needs an STT provider key. Pick
> one:
>
> - **Deepgram** (cloud, paid, easiest) — sign up at deepgram.com, copy
>   the API key.
> - **Whisper local** (free, no key) — use the `WithWhisperStt` builder
>   pointing at a local Whisper model. See
>   [agentassist-setup.md](agentassist-setup.md).
> - **Skip this step** entirely — the toggle still works (you will see
>   the `session.skipped` counter increment), you just will not get
>   transcripts.

Add the provider env var to `docker-compose.full.yml` under `platform-api`:

```yaml
AgentAssist__Provider: Deepgram
AgentAssist__Deepgram__ApiKey: ${DEEPGRAM_API_KEY:-}
```

Restart the API: `docker compose -f docker/docker-compose.full.yml restart platform-api`.

Now flip the runtime toggle **without restarting**. The R5.1 toggle
(`IAgentAssistFeatureToggle`) is consulted at session start:

```bash
# Disable AgentAssist platform-wide. Endpoint: PUT /api/v1/admin/features/agent-assist
# Permission: features:agent-assist:manage (Platform Admin role grants it).
curl -sf -X PUT http://localhost:5000/api/v1/admin/features/agent-assist \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $PLATFORM_JWT" \
    -H "X-Tenant-Id: platform" \
    -d '{"enabled": false}'

# Originate a call. Check metrics — session.skipped should increment.
docker compose -f docker/docker-compose.full.yml exec -T asterisk \
    asterisk -rx "channel originate Local/4001@default application Playback hello-world"

# Re-enable. When enabling you MUST supply provider + credentials (server validates
# both — a body of just {"enabled": true} returns 400). Substitute your DEEPGRAM_API_KEY.
curl -sf -X PUT http://localhost:5000/api/v1/admin/features/agent-assist \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $PLATFORM_JWT" \
    -H "X-Tenant-Id: platform" \
    -d "{\"enabled\": true, \"provider\": \"deepgram\", \"credentials\": {\"apiKey\": \"$DEEPGRAM_API_KEY\"}}"
```

Verify the counter via the Prometheus scrape:

```bash
curl -sf http://localhost:5000/metrics | grep agentassist_session
```

You should see `agentassist_session_skipped_total` incrementing while
disabled.

---

## Step 5 — Audit log viewer + retention admin (~10 min)

The R5.2 + R5.3 release brought first-class admin pages for compliance
operators.

### Audit log

Web UI → **Admin → Audit Log**. Filter:

- **Actor:** `platform@admin.local` to see your own actions.
- **Tenant:** `acme` to see only the work you did on Acme.
- **Action:** `tenant.create`, `tenant.settings.update`, `agent.create`,
  `agentassist.feature.update`.
- **Date range:** today.

Click any row → see the JSON diff payload (`before` / `after`). Note the
`actorId` field is **required** since R5.3 (`HubAuditEntry.ActorId` made
non-nullable; backed by the `sub` JWT claim).

### Retention admin

Web UI → **Admin → Retention**. Three sections:

1. **Targets.** Lists all `IRetentionTarget` registered (session_events,
   completed_sessions, call_attempts, dialer_contacts,
   analytics_interval_snapshots, agent_assist_sessions,
   call_analysis_results). Each shows `windowDays` + `lastRunAt` +
   `rowsPurged` + `dryRun`.
2. **Toggle DryRun.** Default is `true` (safe). Flip per-target to
   `false` to enable real purges.
3. **Run now.** Manually trigger the next scheduled cron tick for one
   target.

> **Production tip:** Leave `DryRun=true` for the first week to confirm
> the purge counts match your expectation, then flip targets one at a
> time. The purge meter
> (`asterisk_sdk_pro_storage_common_retention_rows_purged_total`) is
> exposed at `/metrics`.

### License panel

Web UI → **Admin → License**. Shows the loaded ECDSA license envelope:
issued-to, expiry, enabled features, current `ILicenseGuard` state per
feature, grace-period remaining.

Fresh installs run with `LICENSE_PATH` empty (community / OSS mode — Pro
endpoints respond with HTTP 402 PaymentRequired + RFC 9457 ProblemDetails
carrying actionable `trial_url` and `upgrade_url`). For a production
install, mount a signed `.lic` at `/etc/verbara/license.lic` and set
`LICENSE_PATH=/etc/verbara/license.lic`. Free Tier 0.5 developer licenses
(≤5 agents · ≤1 node · 30-day rolling) are at
https://verbara.io/developer-license.

> Note: `docker-compose.full.yml` keeps `Licensing__EnforcementMode=Disabled`
> for dev/demo back-compat until Pro v2.5.0-pro lockstep migration; the
> deprecated enum still bypasses the gate. Refer to
> [Pro migration guide](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/migration/pro-v240-v250-licensing.md)
> for the v2.5.0-pro path.

---

## Step 6 — Cluster drain demo (~5 min, OPTIONAL — multi-node only)

Skip if running single-node. To enable cluster:

```bash
docker compose -f docker/docker-compose.full.yml --profile cluster up -d
```

> **Scaling caveat.** The bundled compose pins `platform-api` to host port
> `5000:5000`, so `--scale platform-api=N` will fail with a port collision.
> For a real multi-node demo, edit the compose file to remove the host port
> mapping and put a load-balancer in front. Full multi-node walkthrough lives
> in [cluster-management.md](cluster-management.md).

In Web UI → **Platform Admin → Cluster**, you should see 3 healthy nodes.
Click a node → **Drain**. The drain coordinator (`Pro.Cluster`) will:

1. Mark the node `Draining` in the registry.
2. Reject new traffic; existing calls keep running.
3. Show a progress bar (`drainedCalls / totalCalls`).
4. Move to `Offline` when the last call ends.

Watch the audit log: each transition emits `cluster.node.state.changed`
events.

---

## Step 7 — Tenant isolation smoke check (~5 min)

Verify `initech` users see exactly zero `acme` or `globex` data — this
is the multi-tenant correctness guarantee we hardened in R5.2.

```bash
INITECH_JWT=$(curl -sf -X POST http://localhost:5000/api/v1/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"initech","email":"admin@initech.local","password":"InitechAdmin2026!"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")

# Should return [] (initech has no agents)
curl -sf -H "Authorization: Bearer $INITECH_JWT" -H "X-Tenant-Id: initech" \
    http://localhost:5000/api/v1/admin/agents

# Should return 403 — initech user cannot read another tenant's audit log
curl -i -H "Authorization: Bearer $INITECH_JWT" -H "X-Tenant-Id: acme" \
    http://localhost:5000/api/v1/admin/audit
```

The first call returns `[]`. The second should be `403 Forbidden` because
the tenant resolver rejects the cross-tenant header for non-platform-admin
users.

---

## Step 8 — Optional polish

A few one-liners worth knowing for a polished demo:

- **OpenAPI HTML** (R5.3 quick win): `http://localhost:5000/scalar/v1` —
  rendered, browsable spec. (The raw OpenAPI 3.0 JSON is at
  `http://localhost:5000/openapi/v1.json`.)
- **Grafana** (only if you started with `--profile cluster` and added
  Grafana yourself, or via the demo overlay): see
  [dashboards/](dashboards/) — currently ships
  `resilience-overview.json` (R3b/v1.9.1).
- **Revenue dashboard** (R5.3): Web UI → **Platform Admin → Revenue**.
  Shows MRR + per-tenant invoices + dunning state.
- **Tenant Settings editor** (R5.3): Web UI → **Admin → Tenant Settings**.
  Inline edit branding, MFA policy, lockout, password rules.

---

## Cleanup

```bash
docker compose -f docker/docker-compose.full.yml down -v
```

`-v` removes Postgres + recordings volumes. Drop it if you want to keep
the seeded state for the next session.

---

## Where to next

- [agentassist-setup.md](agentassist-setup.md) — full STT/TTS provider
  matrix.
- [cluster-management.md](cluster-management.md) — cluster operations.
- [api-keys-management.md](api-keys-management.md) — scoped tenant + management
  API keys.
- [load-test-baseline.md](load-test-baseline.md) — load + capacity SLOs
  (R5.4 baseline).
- [resilience-runbook.md](resilience-runbook.md) — circuit/retry/timeout
  tuning.
