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
active sessions. The node's row menu offers no item today (see Known
limitations), so start the drain through the API:

1. Send `POST /api/v1/management/cluster/nodes/{nodeId}/drain` with
   `{"gracePeriodSeconds": <n>}` (the Web dialog's default is 300); the
   API answers 202 Accepted. The drain is refused while it would leave
   no other healthy node.
2. The **Active drains** section shows the drain's progress (remaining
   and completed calls, calls force-disconnected) and its state. The
   drawer's **Drain Status** tab does not (see Known limitations).
3. **Cancel** (in the Active drains section) sends
   `DELETE /api/v1/management/cluster/nodes/{nodeId}/drain` and returns
   the node to routing.
4. **Force** (in the Active drains section) ends the remaining calls of
   a running drain
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
- **Row menu, healthy count and Drain Status tab.** The page compares
  node states in title case (`Healthy`, `Draining`) while the API
  returns them in lower case, so these never follow the node's state:
  the row menu shows no Edit, Drain, Cancel, Force or Remove item for
  any node, the summary card counts 0 healthy nodes, and the drawer's
  Drain Status tab always reads "not draining". Until the Web fixes the
  comparison, start a drain, edit a node or remove one through the
  endpoints above, and follow, cancel or force a drain in the Active
  drains section, which reads the drain's own state and is not
  affected.
