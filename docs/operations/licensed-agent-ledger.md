# Licensed-agent ledger and daily close

How Platform records licensed agents: the append-only ledger, the daily close that turns it into the
billable figure, the fixed 15-month retention, and what an operator configures and checks. The upgrade
steps are in [licensed-agent-metering-upgrade.md](licensed-agent-metering-upgrade.md).

A **licensed agent**, at any instant, is an agent row whose user is `Active`, on a **Customer** tenant.
The figure for a day is the largest number of licensed agents at the same instant during that day. The
deployment figure for a day is the sum of the day figures of all Customer tenants. Licensing never blocks
anything because of this count.

## Before the metering release first runs: set the day zone

Days are cut in one time zone for the whole deployment, set with `Licensing:Metering:DayZone` (an IANA
zone id such as `America/Bogota`; environment variable `Licensing__Metering__DayZone`). It defaults to
`UTC`.

**Set it before the release first runs.** The close worker's first tick records the zone with the
deployment chain, and the zone can never change after that:

- If a later start has a different value, the close worker stops at its first tick, before it writes
  anything. It logs a critical error that names both zones (event `7523`), and the readiness check
  (`/health/ready`, `services`) reports `LicenseAgentDailyCloseWorker` unhealthy. Put the recorded zone
  back and restart. The recorded zone is in `license_agent_chain_heads.day_zone` on the `*deployment` row.
- An unknown zone id stops the worker the same way. The runtime image must carry the IANA time-zone
  database; the published image does.

A day runs from local midnight to the next local midnight, so a daylight-saving day lasts 23 or 25 hours.
The check never runs at host start and never runs without PostgreSQL, so tools that boot the host
without a database (such as the OpenAPI export) are not affected.

## The tables

Migration `019_LicenseAgentLedger` adds three tables. They hold ids only: no names, e-mail addresses or
message content.

| Table | One row per |
|-------|-------------|
| `license_agent_chain_heads` | chain: a tenant id, or `*deployment` for the deployment chain. Holds the head (`head_sequence`, `head_hash`), the licence id the chain is anchored to, and, on `*deployment` only, `day_zone`. |
| `license_agent_events` | change that can alter who counts (export: `events[]`). |
| `license_agent_daily` | closed day of a Customer tenant, or deployment total (`tenant_id` null), including every correction revision (export: `daily[]`). |

### Ledger rows

A row is written **in the same database transaction** as the change it records, so both are durable or
neither is. If the ledger row cannot be written, the change fails.

| `kind` | Written when |
|--------|--------------|
| `chain_anchored` | a chain is first used (first row of every chain). |
| `agent_baseline` | a tenant chain is anchored: one per agent row that already exists, with its current `counted`. |
| `chain_reanchored` | the loaded licence id changed (renewal, `PUT /license`, revalidation) since the chain's last row. |
| `agent_created`, `agent_deleted` | an admin creates or deletes an agent, or a GDPR purge deletes one. |
| `user_status_changed` | the status of a user who owns an agent changes. A user without an agent gets no row. |
| `user_deleted` | a GDPR purge deletes a user who owned an agent (after its `agent_deleted` row). |
| `conversation_taken_over`, `conversation_transferred`, `conversation_reassigned` | a supervisor takes a conversation over, a conversation is transferred to an agent, or a supervisor reassigns it to an agent. |

`counted` is the agent's state after the change: `true` when the agent row exists and its user is
`Active`. A takeover, transfer or reassign adds its receiver to the count only when it was not counted
already; it never removes anyone. Agent presence (Available, Break and so on) is not recorded.

The ledger has no tenant-type filter: changes to agents on Partner or Platform tenants are recorded and
exported, but those tenants are never closed (see the audit runbook below).

### The hash chain

Each tenant's events and daily rows form one chain, ordered by a gap-free `sequence`; the deployment
totals form their own chain. Every row carries `prevHash` (its predecessor's `rowHash`) and
`rowHash = "lac1:" + hex(SHA-256(canonical))`, where `canonical` is the fields joined with `|`:

- ledger row: `prevHash|tenantId|sequence|eventId|kind|occurredAt|agentId|userId|actorUserId|conversationId|userStatus|counted|licenseId`;
- daily row: `prevHash|tenantId|sequence|day|revision|licensedAgents|closedAt|closedThroughSequence|licenseId`.

A null renders as the empty string, `true`/`false` in lowercase, an instant in UTC with seven fractional
digits and `Z` (`2026-09-17T13:02:44.0000000Z`; stored instants have microsecond precision), a day as
`yyyy-MM-dd`, and the event id as a lowercase hyphenated GUID. The first row's `prevHash` is the genesis
`"lac1:" + hex(SHA-256("genesis|" + (tenantId or "deployment") + "|" + (licenseId or "")))`. A licence
change never starts a new genesis: it appends `chain_reanchored`, linked to the old head.

The chain makes rewriting evident; it does not prove anything, because the owner controls the database.

### The triggers

`019_` adds `BEFORE UPDATE` and `BEFORE DELETE` triggers on `license_agent_events` and
`license_agent_daily`:

- every `UPDATE` fails;
- every `DELETE` fails, unless the session ran `SET LOCAL verbara.license_purge = 'on'` in the same
  transaction. Only the retention purge does that.

A third trigger keeps `license_agent_chain_heads.day_zone` from ever changing once written. Do not
disable these triggers: rows changed or deleted outside the purge break the chain, and the next audit
reports the first broken `sequence`.

## The daily close

`LicenseAgentDailyCloseWorker` runs on every API replica every 15 minutes. It needs no leader: each chain
is closed under a lock on its head, and `(chain_key, day, revision)` is unique, so a second replica finds
the rows already written.

- **First tick:** checks the day zone (above), then anchors the deployment chain and every Customer
  tenant chain that has no head yet, with their `agent_baseline` rows. A quiet tenant's days are closed
  from the upgrade on.
- **Every tick:** anchors any new Customer tenant, closes every ended day of every Customer tenant, then
  the deployment total once every Customer tenant has closed that day.
- **Which tenants:** every tenant whose type is Customer, **in every status** (`Suspended`, `Warning`,
  `Degraded`, `PendingDeletion`, `Deleted` included) and whatever its parent, including a Customer whose
  `parent_tenant_id` is null or names a missing tenant. Partner and Platform tenants are never closed.
- **Corrections:** a closed day is never rewritten. When a ledger row whose `occurredAt` falls inside an
  already closed day arrives after the close (for example from a replica with a lagging clock), the next
  tick appends a `revision + 1` row with the recomputed figure, and a deployment correction when the
  total changes. Only the last 35 days are checked. Readers use the highest revision of a day.
- **Licence state is never consulted:** the close keeps running with no licence, an expired licence or
  one in grace.

## Retention: 15 months, fixed

Once a day the worker deletes ledger and daily rows older than 15 months (by `occurredAt` for ledger rows,
by `day` for daily rows), on every chain. The window is a constant: tenant retention policies, retention
settings and dry-run flags do not change it, and the tenant retention purge (`tenant_retention_policies`)
never touches these tables. A GDPR purge of a user does not delete its ledger rows either: they keep the
user and agent ids, nothing else, for the window.

The purge removes the oldest part of a chain up to the last row before the first one still inside the
window (a daily row written just after the cut waits for the next run). For each chain it purged, it
writes a `purge_log` row with `subject_type` `license_agent`, `subject_id` the chain key, the counts in
`entities_deleted` (`events`, `daily`, `throughSequence`), and the last purged sequence and its `rowHash`
in `reason`. Verification of the remaining chain starts from that hash: the first remaining row's
`prevHash` equals it.

## Reading the figures and the export

Two read-only endpoints serve the figures, to Platform administrators only (`PlatformAdminOnly`, like
`/management/system/license`); a tenant administrator gets `403`. Both take `from` and `to` as
`yyyy-MM-dd` dates in the day zone; the range may span at most **460 days** (any 15 calendar months fit
one call), and a longer range, `from` later than `to` or a malformed date answers `400`.

- **`GET /api/v1/management/licensing/agents`** — the self-declaration figures. `days[]` lists each day of
  the range whose deployment total is closed, with that total (`deploymentLicensedAgents`,
  `deploymentRowHash`) and each Customer tenant's figure for the day (`tenants[]`: `tenantName`,
  `licensedAgents`, `revision`, `closedAt`, `rowHash`); every cell is the latest revision of its day.
  `deployment.peakLicensedAgents` is the highest daily total of the range and `deployment.peakDay` the
  earliest day that reached it; both are `0` and `null` while no day of the range is closed (for example on
  the first day of a month). `license` is read from the loaded licence once per request; `maxAgents` is the
  signed band and is advisory (`maxAgentsAdvisory` is always `true`). `deployment.overBand` is `true` only
  when `maxAgents` is greater than 0 and the peak exceeds it; it blocks nothing.
- **`GET /api/v1/management/licensing/agents/export`** — the evidence. For every chain (Customer, Partner and
  Platform tenants, and the deployment chain with `tenantId` null) it returns a contiguous run of rows:
  from the row immediately before the first row written in the range, to the last row written in it or the
  last correction of a day inside it, whichever is later. `events[]` and `daily[]` together are that run,
  in `sequence` order per chain, with every correction revision. `chainHeads[]` gives each chain's head;
  when the range ends today, each chain's last exported row is its head. A chain that did not change during
  the range contributes its head row alone. Instants are written exactly as the canonical form hashes them
  (`2026-09-17T13:02:44.0000000Z`) and nulls as `null`, so a verifier renders each row from the JSON
  without reformatting.

Both read one database snapshot, so the rows and the heads agree even while other replicas append. While
the deployment chain is not anchored yet, `dayZone` is the configured `Licensing:Metering:DayZone`;
afterwards it is the recorded zone.

To verify an export offline: per chain, order `events[]` and `daily[]` together by `sequence`; check that
the sequences have no gap; recompute each row's `rowHash` from its canonical form (above); check that each
row's `prevHash` is the previous row's `rowHash` (the first exported row's `prevHash` is taken as given, or
must be the genesis when its `sequence` is 1); and compare the last row with `chainHeads`.

## Audit runbook: what to reconcile

The owner declares the reached band monthly; an audit reconciles the ledger against other signals.

1. **Chain integrity.** Recompute every `rowHash` from the canonical fields and check that each `prevHash`
   is its predecessor's `rowHash`, from the genesis (or from the last `purge_log` hash of the chain). Any
   mismatch names the first altered `sequence`.
2. **Agents on non-Customer tenants.** Their changes are in the ledger (they are exported), but no daily
   row is closed for them, so they never appear in the figures. List them with
   `scripts/tenant-type-misplaced-data.sh` and reconcile them against CDRs and `queue_log`: an agent that
   serves calls from a Partner or Platform tenant serves uncounted.
3. **Secondary signals.** Compare the counted agents with `auth_events`, conversation owners,
   `queue_log`/CDR, the PJSIP endpoints, `webhook_deliveries` and `dialer_license_audit`.
4. The 15-month window fits one export call (the export accepts up to 460 days); see "Reading the figures
   and the export" above for how to verify it.

## Rolling back

Revert the image. The three tables and their triggers stay, inert, and the previous version never writes
to them. **Do not drop them**: they are billing evidence.
