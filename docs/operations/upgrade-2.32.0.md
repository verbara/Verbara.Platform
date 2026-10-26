# Upgrading to 2.32.0

What an operator or an API integrator meets when moving a Platform installation from 2.31.x to 2.32.0, the release
in which the outbound dialer honours DNC lists, callbacks and agent dispositions. 2.32.0 pins Verbara.Sdk.Pro
`2.21.0-pro`, which adds dialer migration V004. It adds one Platform migration, `025_DialerDispositionDispatches.sql`.
On its first start it repairs two kinds of stored campaigns, once, with an audit entry for each campaign it changes.

Every query below that reads `campaigns`, `dnc_lists` or `scheduled_callbacks` runs on the **dialer database**
(`ConnectionStrings:Dialer`, or `ConnectionStrings:Postgres` when no dialer string is set). The queries on
`audit_entries` and `dialer_disposition_dispatches` run on the **Platform database** (`ConnectionStrings:Postgres`).
In most installations both are the same database.

## Before you upgrade

1. **Campaigns whose DNC check the first start will turn on.** Every campaign created before
   `2026-10-23 10:08 UTC` (the first console release that sent `dncEnabled`) and stored with its DNC check off gets it
   on. An API client that created such a campaign with the check off on purpose can turn it off again after the
   upgrade with `PUT /api/v1/admin/campaigns/{id}` and `"dncEnabled": false`; the repair does not run twice.

   ```sql
   SELECT tenant_id, id, name, created_at FROM campaigns
   WHERE check_global_dnc = FALSE AND created_at < '2026-10-23T10:08:00Z'
   ORDER BY tenant_id, id;
   ```

2. **Predictive campaigns whose abandon rate the first start will reset.** A console before v3.26.0-web sent its
   "Target wait (seconds)" value as `targetAbandonRate` for every predictive campaign, so such a campaign stores a
   value above 1 (15 by default). The first start sets it to `0.03`, the console's and the API's default. The dialer's
   pacing does not read the stored rate today, so no dialing changes.

   ```sql
   SELECT tenant_id, id, name, target_abandon_rate FROM campaigns
   WHERE mode = 4 AND target_abandon_rate > 1 ORDER BY tenant_id, id;
   ```

3. **Scheduled callbacks that were never dialed.** Until this release no part of the dialer read
   `scheduled_callbacks`, so every row written by `POST /api/v1/admin/campaigns/{id}/callbacks` (or by a typification
   with a callback date) is still pending. From 2.32.0 they are due on the dialer's first callback poll after the
   upgrade, and they are dialed when their campaign is Active. To see them:

   ```sql
   SELECT tenant_id, campaign_id, contact_id, scheduled_at FROM scheduled_callbacks
   WHERE executed = FALSE ORDER BY scheduled_at;
   ```

   To retire the ones older than a date you choose, so they are never dialed, run this **before** the upgrade:

   ```sql
   UPDATE scheduled_callbacks SET executed = TRUE
   WHERE executed = FALSE AND scheduled_at < '2026-10-01T00:00:00Z';  -- your date
   ```

4. **Global-scope DNC lists owned by a tenant that is not the host tenant.** Since 2.31.1 such a list blocks numbers
   only for its own tenant's campaigns, as a Tenant-scope list does (published as
   [GHSA-wp92-r8xv-5ggm](https://github.com/verbara/Verbara.Platform/security/advisories/GHSA-wp92-r8xv-5ggm) and
   [GHSA-wj85-9pm2-m77q](https://github.com/verbara/Verbara.Platform/security/advisories/GHSA-wj85-9pm2-m77q), both
   fixed in 2.31.1; 2.32.0 carries those fixes). Count them, and change them to `Tenant` with
   `PUT /api/v1/admin/dnc-lists/{id}` if you want their scope to say what they do:

   ```sql
   SELECT count(*) FROM dnc_lists
   WHERE scope = 1 AND tenant_id <> '<host tenant id>';
   ```

   The host tenant's id is the tenant `/api/setup` created (`SELECT tenant_id FROM tenants WHERE type = 0;` on the
   Platform database, or `GET /api/v1/management/tenants`).

## What changes

### DNC

- **`POST /api/v1/admin/campaigns` stores a campaign with its DNC check on when `dncEnabled` is absent or null.** Only
  `"dncEnabled": false` turns it off. `PUT` is unchanged: it applies `dncEnabled` only when present.
- **Platform-wide DNC lists have their own route, `/api/v1/management/dnc-lists`.** The host operator (an
  administrator of the host tenant, or a management API key) creates, updates and fills lists there with the same
  operations as `/api/v1/admin/dnc-lists` (lists, entries, import, check). Every list created there is owned by the
  host tenant, whoever calls, so a `Global`-scope list there blocks its numbers for every tenant's campaigns whose DNC
  check is on. A customer tenant does not see these lists on `/api/v1/admin/dnc-lists`, and their entries answer 404
  there. Before `/api/setup` has created the host tenant the route answers 503 "Platform not initialized" to a
  management key and 403 to any other credential. `/api/v1/admin/dnc-lists` still answers 403 to `"scope": "Global"`.
- **A disposition code can add the dialed number to the tenant's DNC list.** `POST` and `PUT
  /api/v1/admin/campaigns/{id}/dispositions` accept `addsToDnc` (absent on create is `false`; absent or null on update
  leaves it unchanged), and the code's representation carries it. A call typified with such a code puts its number on
  the tenant's agent-disposition DNC list as a non-expiring entry and resolves the contact as do-not-call; no retry or
  callback is scheduled for it.

### Typification of campaign calls

- **A voice Conversation is typified while the call is up or after it ended, once, and the typification no longer
  moves it.** `POST /api/v1/conversations/{id}/typify` on a voice Conversation in `Active` or `WrapUp` answers 200 and
  leaves the state as it was; the voice bridge moves it to `WrapUp` at the hangup and releases the agent's voice
  capacity then. Before 2.32.0 the usual after-call typification answered 409. A second typification of the same voice
  Conversation, and one of a `Closed` one, answers 409 `conversation-not-closable`, and nothing of it is stored.
- **The typification of a campaign call reaches the dialer.** The Conversation of an answered campaign call carries
  the dialer's contact id (`dialerContactId`) and the call's action id (`dialerActionId`); a typification whose leaf
  dialer code matches an active disposition code of the campaign hands the disposition to the dialer: the code and the
  notes on the call's attempt, then a retry, a callback (at `fieldValues.callback_date`, when the leaf or the code asks
  for one) or a do-not-call entry. A leaf code the campaign does not have reaches nothing and is logged as a warning;
  no `campaign.disposition_submitted` event is published for it any more.
- **The typify response says what the dialer did:** a new optional member `dialerDisposition` on the 200 body, one of
  `None` (nothing handed over), `Applied`, `AlreadyApplied`, `AwaitingOutcome` (applied when the call's outcome is
  recorded), `UnknownCall`, or `Pending` (not confirmed in this request; handed over again within seconds). The status
  is 200 once the typification is stored, whatever the dialer answered. No store keeps the member, so every other
  representation of a typification omits it.
- **The disposition owed to the dialer is stored with the typification.** Migration 025 adds the table
  `dialer_disposition_dispatches` (one row per typified campaign call). A hand-off that fails, or that an instance
  stops in the middle of, is taken over by any instance within seconds, or within a minute after an instance stopped;
  it is retried until the dialer answers and is never dropped. The meter `verbara.platform.dialer` exports
  `dialer.disposition_dispatch.completed` (tagged `result`), `dialer.disposition_dispatch.released` and the gauge
  `dialer.disposition_dispatch.pending`. From the tenth attempt every failed hand-off logs a warning naming its
  Conversation. Rows still waiting after an hour:

  ```sql
  SELECT tenant_id, conversation_id, campaign_id, disposition_code, attempts, next_attempt_at, last_error, created_at
  FROM dialer_disposition_dispatches
  WHERE completed_at IS NULL AND created_at < now() - interval '1 hour'
  ORDER BY created_at;
  ```

- **The wrap-up timeout keeps a stored typification (#476).** With autonomous disposition enabled, the timeout used to
  replace the agent's typification of a Conversation still in `WrapUp` with its own AI disposition. It now closes the
  Conversation, keeps the stored typification, and audits `typification.autonomous.skipped` with reason
  `AlreadyTypified`.

### Campaigns and callbacks

- **`POST` and `PUT /api/v1/admin/campaigns` answer 400 to a `targetAbandonRate` outside `[0, 1]`.** An absent or null
  member is unaffected. A console before v3.26.0-web sends its target wait (15 by default) as the rate for a
  predictive campaign, so creating or editing a predictive campaign from such a console fails with 400 until the
  console is upgraded to v3.26.0-web; progressive, power and preview campaigns are unaffected.
- **`POST /api/v1/admin/campaigns/{id}/callbacks` answers 404 when `contactId` is not a contact of that campaign**, and
  schedules nothing. A scheduled callback holds its contact from the moment it is written: the dialer does not dial it
  through the campaign's normal dialing until the callback's time, and dials it once then, on however many replicas.
- **A contact the compliance check rejects no longer blocks the rest of the list:** a DNC match is resolved
  do-not-call, a campaign-wide pause ends the pass, and a frequency limit waits the campaign's time between attempts.

## After the upgrade

The two repairs ran on the first start that reached the dialer database. Each changed campaign has one audit entry in
its tenant's audit log (`GET /api/v1/admin/audit`, or on the Platform database):

```sql
-- campaigns whose DNC check was turned on
SELECT tenant_id, entity_id AS campaign_id, occurred_at FROM audit_entries
WHERE action = 'campaign.dnc_repaired' ORDER BY tenant_id, occurred_at;

-- predictive campaigns whose abandon rate was reset, with the old and the new value
SELECT tenant_id, entity_id AS campaign_id, details->>'oldTargetAbandonRate' AS old_rate,
       details->>'newTargetAbandonRate' AS new_rate, occurred_at
FROM audit_entries WHERE action = 'campaign.abandon_rate_repaired' ORDER BY tenant_id, occurred_at;
```

The repairs record themselves in `platform_one_shot_repairs` on the dialer database (one row per repair, with the
number of campaigns it changed) and do not run again. A database without the dialer schema is left unchanged.

## Rolling back

Roll back to 2.31.1. Dialer migration V004 and Platform migration 025 are additive, and 2.31.1 neither reads nor
writes the new tables; a disposition 2.32.0 left pending is handed over when 2.32.0 runs again. Neither repair is
undone by a rollback: the audit entries list what each changed, and a campaign can be changed back with `PUT`.
