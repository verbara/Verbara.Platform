# Upgrading to licensed-agent metering

What an operator meets when moving a Platform installation to the release that ships licensed-agent
metering: what to check before, what changes, and how to roll back. It covers the identity and routing
part (migration `018_AgentUserUnique`) and the ledger and daily close (migration `019_LicenseAgentLedger`,
described in [licensed-agent-ledger.md](licensed-agent-ledger.md)). The release notes link here.

## Before you upgrade

### 1. Run the identity report

Migration `018_AgentUserUnique` makes each user own at most one agent per tenant. It **refuses to run**
when a user already owns two or more agents in the same tenant. The API then does not start, and its
log names every duplicated pair. The migration never deletes, merges or repoints an agent on its own.

Run the read-only report first, and again after each fix, until its section 1 returns no rows:

```sh
psql "$DATABASE_URL" -f scripts/ops/agents-identity-report.sql
# or, with the bundled compose stack:
docker compose exec -T postgres psql -U platform -d verbara < scripts/ops/agents-identity-report.sql
```

The report has four sections:

1. **Duplicate agents.** Every agent of a user who owns more than one in a tenant, with the references
   it holds: queue memberships, conversations it owns (all, and the open ones), capacity rows and its
   Asterisk interface (`PJSIP/{tenantId}-agent-{agentId}`). The `proposal` column marks the agent the
   procedure below keeps.
2. **Orphan agents.** Agents whose user does not exist. They do not block the migration, but they are
   never offered work, never provisioned in Asterisk and never counted. Delete them, or create the
   user they name.
3. **Orphaned Customer tenants.** Customer tenants whose `parent_tenant_id` is null or names a tenant
   that does not exist. Repair the parent; the licensed-agent count still includes them.
4. **Agents on non-Customer tenants.** See step 2.

### 2. Run the misplaced-data inventory

Run `scripts/tenant-type-misplaced-data.sh` next to the report. It lists agents and other operational
rows on Platform or Partner tenants. Move each such agent to its Customer tenant, or delete it: only
Customer tenants count licensed agents, so an agent left on another tenant serves uncounted.

### 3. Resolve duplicates by hand

For each user in section 1:

1. **Keep one agent:** the one that has an extension; when none or several have one, the oldest
   `created_at`. The report's `proposal = keep` row follows this rule.
2. **Repoint the losers' references to the kept agent**, in this order:
   - queue memberships (`queue_memberships.agent_id`), unless the kept agent already has a membership
     in that queue, in which case delete the loser's;
   - open conversations they own (`conversations.owner_id` where `owner_kind = 2`); closed ones can stay
     as history;
   - remove the losers' rows from `agent_capacity`; the kept agent's capacity is recomputed.
3. **Delete each losing agent** with `DELETE /api/v1/admin/agents/{id}` (this also removes its PJSIP
   endpoint and its Asterisk `queue_members` rows), or with SQL when the API is down.
4. Run the report again.

Keep a note of each losing agent id: it may still appear in `queue_log`, CDRs and recordings.

### 4. Set the day zone

Set `Licensing:Metering:DayZone` (an IANA zone id; environment variable `Licensing__Metering__DayZone`)
**before the release first runs**, if the days of the licensed-agent figures should not be UTC days. The
first run records the zone, and it can never change afterwards: a later start with another value stops
the daily close and reports it unhealthy. See
[licensed-agent-ledger.md](licensed-agent-ledger.md#before-the-metering-release-first-runs-set-the-day-zone).

## What changes

- **A suspended or deactivated user's agent stops receiving work at once.** Routing (queue eligibility
  and the sticky last-agent path, every channel) offers work only to agents whose user is `Active`, and
  a transfer, reassign or takeover to such an agent fails as for an unknown agent.
- **Its phone unregisters at the next reconcile.** The agent's PJSIP endpoint, auth and AOR, and its
  Asterisk `queue_members` rows, are removed by the next realtime reconcile tick. Reactivating the user
  restores them at the following tick; the Platform queue memberships are kept, so the same set
  returns.
- **Suspending a user forces its agent `Offline`** in the same request, and pauses its queue members
  before the response returns. Reactivation leaves the agent `Offline` until the agent signs in.
- **An agent whose user does not exist is never offered work or provisioned** (see orphan agents above).
- New responses:
  - `POST /api/v1/admin/agents` → `404` when the user does not exist, `409` when the user already owns
    an agent in the tenant (`type` `https://verbara.platform/errors/entity-already-exists`).
  - `DELETE /api/v1/admin/users/{id}` → `409` while the user owns an agent
    (`https://verbara.platform/errors/user-owns-agent`); delete the agent first. A GDPR user purge is
    not refused: it deletes the agent, then the user.
  - `DELETE /api/v1/management/tenants/{id}` → `409` while the tenant owns any agent
    (`https://verbara.platform/errors/tenant-owns-agents`); nothing changes.
- Agent creation and deletion are audited as `agent.created` and `agent.deleted` (category `queues`).
- **Every counted change is recorded in the licensed-agent ledger**, in the same transaction as the
  change: agent creation and deletion, a status change or GDPR purge of a user who owns an agent, and a
  takeover, transfer or reassign to an agent. If the ledger row cannot be written, the change fails.
- **The daily close starts at the first run.** It anchors every Customer tenant's chain, with one
  `agent_baseline` row per existing agent, and from then on closes each ended day for every Customer
  tenant (in any tenant status) plus the deployment total.
- **Ledger and daily rows are kept 15 months**, whatever the tenant retention policies, and are purged
  daily after that with a `purge_log` row (`subject_type` `license_agent`).

## Tenant offboarding order

The licensed-agent count does not look at tenant status: an agent of a suspended, pending-deletion or
deleted Customer tenant counts while its user is `Active`, and a deleted tenant can no longer be reached
through the API to clean it up. Offboard a customer in this order:

1. Deactivate its users, or delete its agents.
2. Then suspend or delete the tenant. `DELETE /api/v1/management/tenants/{id}` refuses with `409` while
   agent rows remain.

**Dunning is not covered by that guard.** Dunning moves a tenant to `PendingDeletion` (or `Suspended`)
on its own, without going through the endpoint. Clean a churning customer's agents before dunning
reaches it.

## Tenants with work failover disabled

When a tenant's `WorkFailoverGraceSeconds` is `0` or less, work failover is off. A suspended agent's
conversations then stay owned by it and unserved until an admin reassigns them: the agent is not
counted and cannot act. Set a positive grace, or reassign those conversations by hand after a
suspension.

## Rolling back

Revert the image. The unique index `ux_agents_tenant_user` is harmless to the previous version, which
never creates a second agent for a user on purpose; leave it in place. The ledger tables of `019_` stay,
inert: the previous version never writes to them. Do not drop them; they are billing evidence.
