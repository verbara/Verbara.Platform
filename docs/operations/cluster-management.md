# Cluster Node Management

**Audience:** Platform operators monitoring or draining cluster nodes
via the Web UI. Shipped in Platform v1.10.0 + Web v1.9.0 (R5.1 Phase 2).

## Pages

- **Overview** — `/admin/cluster` lists all registered nodes with
  health state, last heartbeat, version, and routing weight.
- **Node Detail drawer** — click any node row; opens a side drawer
  (uses the `DrawerDetail` primitive shipped in R5.1 Phase 0) with
  three tabs: **Overview**, **Drain**, **History**. Drawer state
  (selected node, selected tab) is reflected in the URL so links can
  be shared.
- **Grafana deeplink** — each drawer carries a "View in Grafana"
  shortcut that opens the Pro.Cluster dashboard filtered to the selected
  node. The deeplink URL template is driven by the
  `GRAFANA_BASE_URL` + `GRAFANA_CLUSTER_DASHBOARD_UID` env vars; leave
  them blank to hide the link.

## Drain flow

Draining a node gracefully removes it from routing without interrupting
active sessions. The Web UI exposes this on the **Drain** tab of the
node detail drawer:

1. The tab shows the node's current drain state (Active / Draining /
   Drained).
2. Clicking **Start Drain** opens a confirm dialog (uses the
   `ConfirmDeleteDialog.confirmationWord` primitive with the word
   **`FORCE`**) — the operator must type `FORCE` to confirm. This
   guards against accidental drains on production nodes.
3. On confirm, the Web sends `POST /api/v1/admin/cluster/nodes/{id}/drain`.
4. The tab refreshes every 5 s showing drain progress (inflight
   session count, expected completion ETA).

The `FORCE` word is intentional — drains cannot be undone gracefully
(canceling mid-drain may strand calls). Operators who genuinely want a
fast drain for an unhealthy node must type the word; there is no
keyboard shortcut.

## History tab

Shows the last N `cluster-node` audit entries for the selected node —
node joined, state transitions, drain start / complete, manual evict.
Backed by the `AuditTrailMini` primitive (shipped R5.1 Phase 2) with
paging. Uses the same `/api/v1/audit/events?subsystem=cluster` endpoint
as other audit views.

## RBAC

Viewing the cluster page + drawer requires `cluster:view`. Starting a
drain requires `cluster:drain:manage`. Both are seeded into
`platform_admin`; operators may also receive them via a custom role.

## Real-time updates

The cluster overview + drawer subscribe to the `admins:platform`
SignalR group (joined automatically for connections carrying the
`PlatformAdmin` role — wired in Pro v1.11.0-pro, Platform v1.9.0).
The typed hub method `OnClusterNodeStateChanged` streams state
transitions; nodes re-render without refresh.

## Status colors (StatusBadge variants)

The Web `StatusBadge` primitive (R5.1 Phase 0) renders node states
with 6 variants:

| State | Variant | When |
|-------|---------|------|
| `Healthy`   | `success`  | heartbeat within threshold, no circuit open |
| `Degraded`  | `warning`  | intermittent failures, still routable |
| `Unhealthy` | `destructive` | circuit open or heartbeat stale > 30s |
| `Draining`  | `info`     | drain in progress, excluded from new routing |
| `Drained`   | `muted`    | drain complete, awaiting removal |
| `Unknown`   | `outline`  | no heartbeat ever observed |

Colors apply everywhere the state is shown (overview table, drawer
header, Grafana link, SignalR notifications).

## Known limitations

- **Drain cancellation is not exposed in the UI** by design — safely
  canceling a drain requires state machine work that hasn't been
  prioritized. Operators needing to un-drain a node can hit
  `POST /api/v1/admin/cluster/nodes/{id}/drain/cancel` via API, but the
  Web does not surface a button. Tracked in **R5.2+** if demand arises.
- **Cluster node force-evict** (remove without drain) is backend-only
  and intentionally has no UI path. Use the API with an audited
  support ticket trail.
