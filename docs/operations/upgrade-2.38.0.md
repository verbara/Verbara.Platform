# Upgrading to 2.38.0

What an operator or an API integrator meets when moving a Platform installation from 2.37.x to 2.38.0, the release
in which an open digital conversation with no message for 72 hours closes by itself. 2.38.0 adds one Platform
migration, `026_ConversationLastMessageAt.sql`, on the **Platform database** (`ConnectionStrings:Postgres`): the
column `conversations.last_message_at`, backfilled for every open conversation, and the nullable column
`tenant_auth_config.inactivity_close_after_hours`. The close is on by default, so the first start after the upgrade
closes every open digital conversation that is already idle past the window. Every query below runs on the Platform
database.

The migration takes a row lock on every row of `conversations` for its backfill, validates `NOT NULL` with a scan under
an `ACCESS EXCLUSIVE` lock, and builds an index that blocks writes to `conversations` while it runs. On a large table,
upgrade in a quiet window.

## Before you upgrade

1. **What the first pass will close, by tenant and channel.** A conversation counts when it is not voice, is in
   `Active`, `OnHold`, `Consulting`, `WaitingForCustomer` or `Resolved`, and its last customer-facing message (an
   inbound message, or an outbound one not refused before the send) is older than 72 hours, or it has no such message
   and was created more than 72 hours ago. This is the same rule the migration uses to backfill the clock. The query
   uses the host default of 72 hours; for a tenant with an override (below), use its window.

   ```sql
   SELECT c.tenant_id, c.channel, count(*) FROM conversations c
   WHERE c.channel <> 0 AND c.state IN (10, 11, 12, 30, 40)
     AND COALESCE((SELECT max(m.created_at) FROM messages m
                   WHERE m.tenant_id = c.tenant_id AND m.conversation_id = c.conversation_id
                     AND (m.direction = 0 OR (m.direction = 1 AND m.delivery_status <> 3))),
                 c.created_at) < now() - interval '72 hours'
   GROUP BY c.tenant_id, c.channel ORDER BY 3 DESC;
   ```

2. **Webhook subscriptions that will receive each close.** Every closed conversation sends one
   `conversation.state_changed` delivery to each active subscription of its tenant that lists that event:

   ```sql
   SELECT count(*) FROM webhook_subscriptions WHERE event_types ? 'conversation.state_changed' AND is_active;
   ```

   During the first wave, a subscription whose endpoint does not answer can delay the deliveries of other tenants until
   its circuit opens. How long is not measured yet. If you have a large backlog and such an endpoint, fix or deactivate
   the subscription first, or stage the wave with a smaller `Conversations:InactivityCloseBatchSize`.

3. **More than one Api replica** (the Helm chart's default is 2): set `Conversations:InactivityCloseEnabled=false`
   for the upgrade (below, "How to switch it off"), and turn it on with a restart once every replica runs 2.38.0. An
   older replica stores messages without advancing the clock, so a replica of the new version could close a
   conversation that is still active on an old one. The single-container SMB compose has no such window.

## What changes

### The inactivity close

- **An open digital conversation with no message for the window closes by itself.** A non-voice conversation in
  `Active`, `OnHold`, `Consulting`, `WaitingForCustomer` or `Resolved` whose last customer-facing message, from either
  side, is older than the window goes straight to `Closed`, without `WrapUp`. Its agent's capacity unit is released. An
  internal note does not count as a message, nor does a send refused before the provider (stored `Failed`).
  `Snoozed`, `Escalated`, `WrapUp`, `Queued`, `Offered` and every voice conversation are left alone.
- **It is silent.** The customer gets no message and no CSAT survey.
- **What each close leaves behind:**
  - one `conversation.state_changed` webhook delivery (`→ Closed`) per closed conversation;
  - the audit entry `conversation.closed` with the actor `verbara:system:inactivity-close`, actor type `system`, and
    the reason and previous state in its details;
  - `metadata._closedReason` on the conversation: `inactivity`, or `inactivity-backlog` (below);
  - the conversation appears in reports as closed;
  - one increment of the counter `conversations.system_closed` of the meter `verbara.platform.conversations`
    (Prometheus `conversations_system_closed_total{reason="inactivity"|"inactivity-backlog"|"customer-returned"}`);
  - one Information log line with the tenant, the channel, the reason and the idle-age bucket at the close: `<72h`
    (only with a window below 72 hours), `72-96h`, `96-168h` or `>168h`.
- **Backlog marking.** A close records `inactivity-backlog` when the conversation had been idle for more than the
  window plus a grace when the sweep reached it, and `inactivity` otherwise. The grace is the larger of one hour and
  twice `InactivityCloseSweepIntervalSeconds` (one hour at the defaults). So the first wave after the upgrade is marked
  `inactivity-backlog`, except conversations idle for less than one hour past the window at that moment, which close
  as `inactivity`. The same mark applies to the wave after the switch was off for a while, or after a tenant's window
  was shortened. A webhook consumer can filter the wave by reading `metadata._closedReason`.
- **Pace.** Each Api replica runs the inactivity pass at most once per `InactivityCloseSweepIntervalSeconds` and closes
  at most `InactivityCloseBatchSize` conversations per tenant per pass, oldest first. Every replica runs it; each
  conversation is closed once.
- **At every start**, before its first pass, each replica advances the clock of every open conversation from the
  messages stored for it (see "Rolling back"), and logs how long that took and how many rows it advanced.

### Configuration

| Key (`Conversations` section) | Default | Range |
|---|---|---|
| `InactivityCloseEnabled` | `true` | |
| `InactivityCloseAfterHours` | `72` | at least `1` |
| `InactivityCloseSweepIntervalSeconds` | `60` | `5` to `3600` |
| `InactivityCloseBatchSize` | `100` | `1` to `1000` |

A value out of range fails the start with a message that names the key. A window below 24 hours is allowed but logs a
warning at start: it closes WhatsApp conversations inside Meta's 24-hour customer-service window, so a customer who
writes again within those 24 hours starts a new conversation. The SMB compose maps the switch, the batch size and the
interval; the window is not mapped.

### A per-tenant window

A tenant can have its own window, set by SQL (no endpoint or console field sets it yet):

```sql
UPDATE tenant_auth_config SET inactivity_close_after_hours = 48 WHERE tenant_id = '…';
-- back to the host default:
UPDATE tenant_auth_config SET inactivity_close_after_hours = NULL WHERE tenant_id = '…';
```

A tenant with no row in `tenant_auth_config` uses the host default. A value below 1 is ignored (the host default
applies) with one warning per tenant per process; a value below 24 logs the same WhatsApp warning, once per tenant per
process. The change is not immediate: each replica applies it on the first inactivity pass that starts at least one
cache lifetime (60 seconds) after the `UPDATE`, so within that 60 seconds plus one sweep interval plus one 5-second
tick: about 2 minutes at the defaults, up to about 61 minutes with `InactivityCloseSweepIntervalSeconds=3600`.
Replicas apply it independently; a restart applies it at once.

### Other changes in the same release

- **A customer message to a `Resolved` conversation closes it**, with `metadata._closedReason` = `customer-returned`,
  the audit entry `conversation.closed` by `verbara:system:customer-returned` and one `conversation.state_changed`
  delivery, and opens a new conversation that routes like any first message. No CSAT survey is sent for that close.
- **A message that arrives while its conversation is being closed or purged opens a new conversation** instead of
  being written into the closed or deleted one. An agent or bot reply to a conversation closed in the meantime is
  refused with the existing errors, and nothing is sent.
- **The queue timeout counts from when the conversation entered the queue**, not from its creation. A conversation
  queued before the upgrade keeps counting from its creation.

## How to switch it off

Set the switch to `false` and restart the Api:

- SMB compose: `INACTIVITY_CLOSE_ENABLED=false` in `.env.reference-smb` (with `INACTIVITY_CLOSE_BATCH_SIZE` and
  `INACTIVITY_CLOSE_SWEEP_INTERVAL_SECONDS` beside it);
- Helm: `--set api.conversations.inactivityClose.enabled=false` (and `batchSize`, `sweepIntervalSeconds`);
- any other deployment: the environment variable `Conversations__InactivityCloseEnabled=false`.

Conversations already closed stay closed.

## After the upgrade

Closes by tenant and reason:

```sql
SELECT tenant_id, metadata->>'_closedReason' AS reason, count(*) FROM conversations
WHERE state = 50 AND metadata->>'_closedReason' IN ('inactivity', 'inactivity-backlog', 'customer-returned')
GROUP BY 1, 2 ORDER BY 1, 2;
```

Their audit entries:

```sql
SELECT tenant_id, entity_id AS conversation_id, performed_by, details->>'reason' AS reason, occurred_at
FROM audit_entries
WHERE action = 'conversation.closed'
  AND performed_by IN ('verbara:system:inactivity-close', 'verbara:system:customer-returned')
ORDER BY occurred_at DESC LIMIT 100;
```

The counter `conversations_system_closed_total` and the close log's idle-age buckets show the same wave.

## Rolling back

Roll back to 2.37.x. Migration 026 is additive: 2.37.x never names either column, its inserts get the column's
default (`now()`), and its updates leave both columns as they are. Closed conversations stay closed.

- **The clocks freeze during a rollback.** The older binary stores messages without advancing `last_message_at`.
- **A re-upgrade does not run migration 026 again.** Instead, every start of 2.38.0 advances each open conversation's
  clock from the messages stored for it before its first sweep, so a conversation that was active under the older
  binary is not closed.
- **The switch-off rule applies to the re-upgrade too:** with more than one replica, keep
  `InactivityCloseEnabled=false` until every replica runs 2.38.0, then turn it on with a restart.
- **Two limits of that reconcile.** An inbound message that the older binary stored with a provider timestamp older
  than the frozen clock does not advance it, so its conversation can close early by at most that delivery delay. A
  send the older binary stored as `Failed` does not count as activity.
