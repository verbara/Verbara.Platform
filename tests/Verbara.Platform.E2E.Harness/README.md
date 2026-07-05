# Verbara.Platform.E2E.Harness

Walking-skeleton end-to-end harness for the Realtime SignalR exactly-once
delivery contract introduced by [ADR-0022 Phase A.5](../../docs/decisions/0022-platform-api-aot-shipping-path.md).

## Why it exists

Plan B Talos smoke test (2026-05-24) closed with **5/6 PASS + 1/6 PARTIAL** —
[smoke report](../../docs/operations/phase-a5-talos-smoke-test-2026-05-24.md).
The PARTIAL was **Test 5 SignalR exactly-once**: the lab had no SignalR client
traffic AND no deterministic source-of-truth for "this pod forwarded / those
pods skipped". This harness ships the missing half — connects real SignalR
clients, triggers real events, and asserts the leader-gate invariant against
the [`/admin/realtime/audit`](../../src/Verbara.Platform.Realtime/Endpoints/AdminRealtimeAuditEndpoint.cs)
endpoint that ships in [PR #18](https://github.com/verbara/Verbara.Platform/pull/18).

## Scope (walking skeleton)

ONE scenario: `exactly-once`. ONE topology: `talos`.

| Scenario | What it asserts | Status |
|---|---|---|
| `exactly-once` | Each connected SignalR client receives exactly N events; per-pod audit shows exactly 1 Forwarded on the leader + N×(pods-1) SkippedNotLeader across followers; exactly one pod is identified as Leader | ✅ Shipped (this PR) |
| `leader-failover`, `multi-pod-fanout-scale`, `multi-leader-independence`, `security-rate-limit-burst`, `security-jwt-abuse`, `security-slowloris`, `chaos-pod-kill` | Each future scenario PR | ⏳ Deferred |

The full framework with source-generated scenario registry, Spectre.Console.Cli,
Aspire dev-loop topology, and CI cascade lands in subsequent PRs — see
[`docs/plans/completed/2026-05-24-e2e-harness-realtime-signalr.md`](../../docs/plans/completed/2026-05-24-e2e-harness-realtime-signalr.md).

## Prereqs

1. **kubectl + KUBECONFIG** pointed at the Talos lab.
2. **Platform v2.4.4+ deployed** in the cluster (audit endpoint = PR #18).
3. **Seeded test users in the test tenant:**
   - One agent user with the `Agent` role + an `agents` table record (required for `PUT /api/v1/agents/me/state`).
   - One platform-admin user with the `PlatformAdmin` role (required for `GET /admin/realtime/audit`).

## Run against Talos lab

```bash
export HARNESS_API_BASE_URL=http://api.r55.local
export HARNESS_REALTIME_HUB_URL=http://api.r55.local/hubs/platform
export HARNESS_TENANT=acme
export HARNESS_AGENT_EMAIL=agent@acme.local
export HARNESS_AGENT_PASSWORD=...
export HARNESS_PLATFORMADMIN_EMAIL=admin@platform.local
export HARNESS_PLATFORMADMIN_PASSWORD=...
# optional overrides:
# export HARNESS_CLIENT_COUNT=5
# export HARNESS_EVENT_COUNT=10
# export HARNESS_SETTLE_SEC=5
# export HARNESS_NAMESPACE=r55-platform
# export TALOS_CONTEXT=admin@asterisk-platform

./scripts/run-harness-talos.sh
```

The wrapper:
1. Discovers Realtime pods via `kubectl get pods -l app.kubernetes.io/name=platform-realtime`.
2. Starts one `kubectl port-forward` per pod (local ports `15031`, `15032`, …).
3. Waits for each to respond on `/health`.
4. Exports `HARNESS_AUDIT_BASE_URLS=http://localhost:15031,http://localhost:15032,...`.
5. Runs `dotnet run --project tests/Verbara.Platform.E2E.Harness -c Release`.
6. Tears down port-forwards on exit.

Reports land under `harness-reports/<timestamp>/exactly-once.{json,md}`.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | PASS — every assertion satisfied |
| 1 | FAIL — scenario invariant violated (see `Failures` section of the .md report) |
| 2 | ERROR — env vars missing, login failed, port-forward failed, etc. |
