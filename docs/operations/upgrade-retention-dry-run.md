# Turning on the Pro retention purge

What an operator meets when the release that adds the `Retention:DryRun` configuration key reaches an installation,
and how to turn the nightly Pro retention purge from counting into deleting. Until this release no setting could make
that purge delete anything: it always ran dry. The upgrade itself changes nothing; deleting starts only when you set the
key to `false` and restart.

The key governs only the Pro retention purge, the nightly job (02:00 UTC) over the Pro tables listed below. Platform's
per-tenant conversation retention (each tenant's conversation retention policy) does not read it and runs as before.

## 1. Nothing changes on upgrade

An absent or empty `Retention:DryRun` means `true`: the purge counts the rows past each table's window and deletes
none, as before. `appsettings.json` now carries `"Retention": { "DryRun": true }` to make the default visible. The Api
logs the mode once at startup:

- `Pro retention purge runs in dry-run mode — set Retention:DryRun=false to delete expired rows.` (Information)
- `Pro retention purge deletes expired rows (Retention:DryRun=false).` (Warning)

A value other than `true` or `false` (any case) stops the Api at startup with an error that names `Retention:DryRun`.
It never falls back to either mode, so a typo cannot leave the purge counting while you believe it deletes.

## 2. Preview what the first real run deletes

Before you set `false`, run the purge by hand in dry-run mode:

```bash
curl -s -X POST -H "Authorization: Bearer $MGMT_KEY" \
  https://<api-host>/api/v1/management/retention/run-now | jq '.targets[] | {name, rowsPurged}'
```

Without a `dryRun` parameter the manual run deletes nothing. Each target's `rowsPurged` is the number of rows the first
real run deletes from that table. The caller needs `system:retention:manage`. You can also watch
`retention_dry_run_would_purge_total` on `/metrics` for one night: it counts the same rows, per target.

## 3. Know what the first real run deletes

The first real run deletes, in batches of 10 000 rows per statement, **every row already past its table's window** —
not only one day's worth. With an analytics database configured, the purge covers the first six tables below; with
the dialer configured, also the last two:

| Target | Window |
|---|---|
| `call_analysis_queue_ended` (failed and not-analysed analysis queue entries) | 30 days |
| `session_events` | 90 days |
| `completed_sessions` | 180 days |
| `agent_assist_sessions` (with their suggestions and compliance alerts) | 180 days |
| `analytics_interval_snapshots` | 365 days |
| `call_analysis_results` | 365 days |
| `call_attempts` (dialer) | 60 days |
| `dialer_contacts` (dialer; only contacts in a final status, with their attempts and phones) | 90 days |

So the windows run from 30 days to 365 days. Live queue snapshots (a 24-hour table) are not among them: this host does
not register their retention target, so the purge never touches them, in either mode.

**Take a backup first** ([Backup and disaster recovery](backup-disaster-recovery.md)). A deleted row cannot be
recovered from the Api.

## 4. Switch it on

Set `Retention__DryRun=false` on **every** Api replica (environment, Helm values or compose file) and restart them.
Then confirm on each replica:

- the startup log shows `Pro retention purge deletes expired rows (Retention:DryRun=false).`;
- `GET /api/v1/management/retention/config` answers `"dryRun": false`.

The answer of that `GET` is the mode of the replica that served it. Replicas started with different values purge
differently, and the `RetentionDryRunDivergence` alert is not a reliable check for that, so compare the key on each
replica. To go back to counting, set `true` (or remove the key) and restart.

## 5. Scripts that called the old toggle

`PATCH /api/v1/management/retention/config` is removed. It never changed the nightly purge; it only flipped a
per-process flag that `GET .../config` reported. A script that calls it now gets `405 Method Not Allowed`: remove it.
The console's DryRun checkbox called it too and answers 405 until the console release that removes the checkbox.
`POST /api/v1/management/retention/run-now?dryRun=false` is unchanged and still deletes for real at once, whatever the
configured mode; it is audited at warning severity.
