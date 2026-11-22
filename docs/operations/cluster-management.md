# Cluster Node Management

**Audience:** Platform operators monitoring or draining cluster nodes
via the Web UI. Shipped in Platform v1.10.0 + Web v1.9.0 (R5.1 Phase 2).

## Pages

- **Overview** — `/admin/cluster` lists all registered nodes with
  health state, capacity, routing weight and Asterisk version, plus
  summary cards (nodes, channels, agents, instances) and an
  **Active drains** section while any drain runs.
- **Node Detail drawer** — click any node row; opens a side drawer
  (uses the `DrawerDetail` primitive shipped in R5.1 Phase 0) with
  three tabs: **Overview**, **Drain Status**, **History**.
- **Grafana deeplink** — the drawer's Overview tab carries an
  "Open in Grafana" link to the Pro.Cluster dashboard filtered to the
  selected node (`var-node=<nodeId>`). It is driven by the Web build's
  `VITE_GRAFANA_URL`; leave it blank to hide the link.

## Drain flow

Draining a node gracefully removes it from routing without interrupting
active sessions:

1. From the node's row menu, **Drain** opens a dialog asking for the
   grace period in seconds (default 300).
2. On submit, the Web sends
   `POST /api/v1/management/cluster/nodes/{nodeId}/drain` with
   `{"gracePeriodSeconds": <n>}`; the API answers 202 Accepted. The
   drain is refused while it would leave no other healthy node.
3. The **Active drains** section and the drawer's **Drain Status** tab
   show the drain's progress (remaining and completed calls, calls
   force-disconnected, deadline).
4. **Cancel** (in the Active drains section) sends
   `DELETE /api/v1/management/cluster/nodes/{nodeId}/drain` and returns
   the node to routing.
5. **Force** ends the remaining calls of a running drain
   (`POST /api/v1/management/cluster/nodes/{nodeId}/force-drain`). Its
   confirm dialog (the `ConfirmDeleteDialog.confirmationWord` primitive)
   asks the operator to type **`FORCE`**, because forced calls are cut.

## History tab

Shows the last 10 audit events whose resource type is `cluster_node`
and whose resource id is the selected node, through the
`AuditTrailMini` primitive (shipped R5.1 Phase 2). Platform writes no
audit entry of that type today, so the tab is empty: no audit trail of
cluster node transitions or drains exists.

## Access

The page requires the `platform:cluster:manage` permission. Every
`/api/v1/management/cluster/*` endpoint is gated by `PlatformAdminOnly`:
only an administrator of the host (Platform) tenant reaches them;
Customer and Partner tenant callers never do.

## Updates

The page refreshes cluster status, nodes and instances over REST every
10 s. There is no live push of node state.

## Status colors (StatusBadge variants)

The Web `StatusBadge` primitive (R5.1 Phase 0, variant `cluster-node`)
renders the node states the API returns (lower case):

| State | Color | When |
|-------|-------|------|
| `healthy`   | green | heartbeat within threshold, no circuit open |
| `degraded`  | amber | intermittent failures, still routable |
| `unhealthy` | red   | circuit open or heartbeat stale |
| `draining`  | blue  | drain in progress, excluded from new routing |
| `offline`   | gray  | out of routing (for example after a drain) |
| `unknown`   | gray  | no heartbeat ever observed |

Colors apply everywhere the state is shown (overview table, Active
drains section, drawer).

## Known limitations

- **Cluster node removal** (`DELETE /api/v1/management/cluster/nodes/{nodeId}`)
  is meant for an `offline` or `unhealthy` node; drain a healthy node
  first.
- **Row menu and Drain Status tab casing.** The page compares node
  states in title case (`Healthy`, `Draining`) while the API returns
  them in lower case, so the row menu's state-dependent actions, the
  healthy count and the drawer's Drain Status tab may not reflect the
  node's state. Until the Web fixes the comparison, drive a drain with
  the endpoints above; the Active drains section is not affected.
