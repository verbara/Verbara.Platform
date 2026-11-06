# Returning-customer routing to the last agent

A customer who writes again by WhatsApp, WebChat or another digital channel can be offered first to the agent who
last owned one of their conversations, when that agent can take it now. If that agent cannot, the conversation goes to
round robin at once, with no wait. It is **off by default**: an upgrade changes no routing until you turn it on for the
host or for a queue. Voice calls do not use it. Platform/ADR-0049 records the decisions.

Every query below runs on the **Platform database** (`ConnectionStrings:Postgres`).

## Host settings

| Key (`InboundRouting:LastAgent` section) | Default | Range |
|---|---|---|
| `Enabled` | `false` | `true` or `false` |
| `WindowHours` | `72` | `1` to `720` |

A value out of range, or one that is not a boolean or a whole number (`yes`, `72h`), fails the start with a message
that names the key. They are read once at start-up. Neither the SMB compose file nor the Helm chart maps them; set the
environment variables `InboundRouting__LastAgent__Enabled` and `InboundRouting__LastAgent__WindowHours` on the Api.

## A per-queue override

A queue can turn it on or off regardless of the host switch, set by SQL (no endpoint or console field sets it yet):

```sql
-- on for this queue, even with the host switch off:
UPDATE queue_configs SET last_agent_routing_enabled = true  WHERE tenant_id = '…' AND queue_id = '…';
-- off for this queue, even with the host switch on:
UPDATE queue_configs SET last_agent_routing_enabled = false WHERE tenant_id = '…' AND queue_id = '…';
-- back to the host switch:
UPDATE queue_configs SET last_agent_routing_enabled = NULL  WHERE tenant_id = '…' AND queue_id = '…';
```

The column is `NULL` for every queue after the upgrade and for every queue created later; no save of the application
writes it, so editing the queue in the console keeps it. The effective switch is the queue's value when it is set, else
the host's. It is read for the queue the conversation is finally routed to: when business hours send a conversation to
their overflow queue, the overflow queue's value decides. The queue is read on every route with no cache, so an `UPDATE`
applies to the next routed conversation, on every replica.

Which queues are on:

```sql
SELECT tenant_id, queue_id, name, last_agent_routing_enabled FROM queue_configs
WHERE last_agent_routing_enabled IS NOT NULL ORDER BY tenant_id, name;
```

## Who counts as the last agent

- The agent who owns the customer's conversation with the most recent customer-facing activity
  (`conversations.last_message_at`), on **any channel** and in any state except `Merged` and `Spam`. A conversation still
  open with that agent counts too, so a customer an agent is serving on WebChat who writes by WhatsApp is offered to the
  same agent, within that agent's chat capacity.
- Only activity inside the window counts: `WindowHours`, or the tenant's **inactivity window** when that is longer and
  the inactivity close is on (`Conversations:InactivityCloseEnabled`, the host's `Conversations:InactivityCloseAfterHours`
  or the tenant's `tenant_auth_config.inactivity_close_after_hours`; see `upgrade-2.38.0.md`, "A per-tenant window"). That
  keeps the customer-returned close of a `Resolved` conversation inside the window for a tenant whose inactivity window is
  above 72 hours. With the inactivity close off, `WindowHours` applies as set.
- Only the last owner is known: an agent who handled the conversation before a transfer does not count.
- No preference is taken for a contact shared by many callers (the withheld-caller contact), nor for a WebChat visitor
  whose identity is not verified.

## When the last agent is offered it

The last agent is offered the conversation when the queue distribution can offer it work now: `Available` or `Busy`, a
free unit of chat capacity, no pending pause, an `Active` user, at least one of the routed queue's required skills when
it has any, and a membership in the routed queue or in any other queue of the tenant. An agent's own row in the routed
queue decides for that queue: if it excludes the agent, or lists channels that do not include the conversation's
channel, the agent is not offered the conversation, whatever their other queues say. In `Break`, `ACW`, `Offline`, at
capacity or suspended, the conversation goes to round robin in the same distribution pass.

The preference is used once. Whoever receives the first offer, a reject or an expired offer sends the conversation back
to round robin, never again to the preferred agent because of the preference. A WebChat visitor who reloads the page
opens a new session, and its first message does not route the conversation again: an open offer stays with its agent and
a conversation waiting in its queue keeps its place. A routing lookup that fails never fails the
route: the conversation is routed with no preference and a warning is logged.

## What it leaves behind

- On the conversation, visible on `GET /api/v1/conversations/{id}`: `metadata._preferredAgentId` (the preferred agent's
  id) while it waits for its first offer, then `metadata._preferredAgentOutcome` = `offered-preferred` or `fallback`
  once that offer is made. These keys are internal; do not build on them.
- The counter `routing.last_agent` of the meter `verbara.platform.routing`, tag `outcome` (Prometheus
  `routing_last_agent_total{outcome}`): `disabled`, `no-history`, `skipped-identity`, `lookup-failed` and `stamped` once
  per routing attempt, then `offered-preferred` or `fallback` once per conversation that carried a preference and was
  offered. A conversation routed twice (an assignment that failed and was retried by the next message) counts two
  routing outcomes. There is no tenant or queue tag.
- One `Information` log line per routing outcome other than `disabled` (event ids 7701 to 7703, `[LAST-AGENT]`), and a
  `Warning` (7704) with the exception type when the lookup fails.

How the preference worked out, by tenant, over the last day:

```sql
SELECT tenant_id, metadata->>'_preferredAgentOutcome' AS outcome, count(*) FROM conversations
WHERE metadata ? '_preferredAgentOutcome' AND created_at > now() - interval '1 day'
GROUP BY 1, 2 ORDER BY 1, 2;
```

## Upgrading and rolling back

Migration `030_QueueLastAgentRouting.sql` adds the nullable column `queue_configs.last_agent_routing_enabled`. It changes
only the catalog, under a brief lock on `queue_configs`, and rewrites no row. A rollback to the previous image leaves the
column, which that image never reads or writes; to remove it:

```sql
ALTER TABLE queue_configs DROP COLUMN IF EXISTS last_agent_routing_enabled;
```

The same release publishes one `conversation.offered` per offer on the live stream; before, an offer made by the queue
distribution was published twice.
