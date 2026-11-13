# Upgrading to the fenced event store (Verbara.Sdk.Pro 2.26.0-pro)

What an operator meets when moving a Platform installation to the release that pins Verbara.Sdk.Pro `2.26.0-pro`, the
release in which each call is recorded once. Only the voice leader writes call events, and the event store checks that
it still holds the voice leader lease inside each write's own transaction. The release adds a **new startup
precondition**: it is a **minor** release, never a patch. It adds no Platform migration. The Pro event-store migration
that `UsePostgresEventStore` runs at registration adds columns and a table on the event store's database (the Pro
`CHANGELOG.md` for `2.26.0-pro` names them). The release notes link here.

Most installations need no action. Every compose file and Helm values file this repository ships puts the lease and the
event store on one connection string, so the new check issues no query. Read on if you set `ConnectionStrings:Cluster`,
`ConnectionStrings:Analytics` or `ConnectionStrings:Dialer` yourself.

## The startup precondition

The event store's fence locks the voice leader lease row (`voice:ami:owner:leader` in the Sdk table
`cluster_distributed_lock`) inside each append's own transaction. So the event store's database must be the database
that holds the lease. The Api resolves two connection strings:

| Role | Resolved from, first one set wins |
|---|---|
| Voice leader lease | `ConnectionStrings:Cluster`, then `ConnectionStrings:Postgres` |
| Event store | `ConnectionStrings:Analytics`, then `ConnectionStrings:Dialer`, then `ConnectionStrings:Postgres` |

Both fallback chains count. **`ConnectionStrings:Dialer` alone can split them:** with `Analytics` unset, the event
store lives in the dialer's database. A dialer string that names a database other than the cluster's therefore moves the
event store away from the lease, even though no setting says "event store".

The precondition applies **only when clustered voice is on**, that is when `Asterisk:Ami:Hostname` is set (that is what
registers the voice leader lease), and both strings resolve to something. A host without AMI is not checked. An
ARI-only host registers no AMI lease and has no AMI-fed call sessions to fence, so it is not checked either.

### When the two strings are equal: nothing runs

When the two resolved strings are identical, character for character, after the Api adds its pool defaults, the Api
issues **no query** for this check: they are one data source. This is the case for every shipped compose file and for
the Helm chart (the Api deployment sets only `ConnectionStrings__Postgres`).

### When they differ: a round-trip probe that refuses to start

When the strings differ in any way, a second host name, a pooler in front, another user or only a different key order,
the Api runs a round-trip probe at registration. The probe runs after the core Platform migration on
`ConnectionStrings:Postgres` and before the dialer's and the event store's schema migrators, so it answers before
either of them can fail with a bare error. Steps:

1. **Lease database:** ensure the lock schema (idempotent, the same call the Api already makes later at start-up),
   delete this instance's expired probe rows, then insert a probe row with the resource
   `verbara:db-identity-probe:<instance>:<random>`, which expires one minute later.
2. **Event store's database**, in one transaction: lock that row with the fence's own
   `SELECT … FOR NO KEY UPDATE`, write a random token into it, commit.
3. **Lease database:** read the token back.
4. **Lease database:** delete the row.

Each statement times out after 10 seconds and the whole probe after 30. Any error or mismatch **refuses to start**: the
Api exits with `EventStoreLeaseColocationException`. Its message names the step that failed, the database name and
`host:port` of each side (never a password), the configuration key that supplied each (for example
`ConnectionStrings:Dialer (fallback for Analytics)`), and, when it can read them, informational server facts such as
`system_identifier` and `pg_is_in_recovery()`. Those facts never decide the outcome. The same database reached through
a transaction-mode pooler on one side and the direct host on the other passes: the probe never compares host names,
ports or database names.

| Refusal | What it means | Fix |
|---|---|---|
| Step 2, `0 rows` ("another database or another cluster") | The event store's string reaches a database that does not hold the lease table's rows | Point both keys at one database |
| `42P01` (no `cluster_distributed_lock`) | The event store's database is not the lease's database | Point both keys at one database |
| Step 3, the token does not match | The two strings reach different databases that both have the table, for example a restored copy | Point both keys at one database |
| `25006` at the locking `SELECT` (read-only transaction) | The event store's string reaches a standby or a read replica. A plain read would have found the row there, so only the lock proves it is the primary | Point the event store's key at the primary that holds the lease |
| `42501` (permission denied) | The event store's role cannot read, lock or update `cluster_distributed_lock` | `GRANT SELECT, UPDATE ON cluster_distributed_lock TO <event store role>;` on that database (a `FOR NO KEY UPDATE` row lock needs `UPDATE`), or use the lease's role for both strings |
| Unreachable or timed out | One side does not answer within the timeouts | Fix the reachability, as below |

The probe needs no privilege the lease's role does not already use. The Sdk lock already inserts, updates, reads and
deletes rows of `cluster_distributed_lock`, and already ensures its schema. The event store's role needs `SELECT` and
`UPDATE` on that table, which is exactly what the fence needs on every append.

A probe that is killed half-way can leave its row behind. The row's resource never matches a lease, so it is inert. The
next probe of the same instance deletes it once expired; the instance is `Cluster:Leadership:InstanceId`, or the
machine name. Rows of a pod name that never comes back can be removed by hand at any time:

```sql
DELETE FROM cluster_distributed_lock
 WHERE resource LIKE 'verbara:db-identity-probe:%' AND expires_at < now();
```

### This adds no coupling to a down database

The Api already fails to start while the event store's database is down. The dialer's and the event store's schema
migrators open a connection to it at registration, before the Api builds. The probe adds no coupling for that case.
When the strings differ, it fails first, with the diagnostic above instead of a migrator's connection error. What is
new is the refusal of a database that is **reachable but wrong**: another database, a standby, or a role without the
fence's rights. Before this release such an installation started and wrote call records unfenced. With the fence it
would lose them silently, so it refuses instead.

## Before you upgrade

1. **Work out both strings** from the table above for every Api replica, with the values your deployment actually
   sets. If they are identical, there is nothing to do.
2. **If they differ but name one database**, through a pooler and the direct host, say, the probe runs on every start
   and passes. Check the event store's role:

   ```sql
   -- on that database
   SELECT has_table_privilege('<event store role>', 'cluster_distributed_lock', 'SELECT') AS can_select,
          has_table_privilege('<event store role>', 'cluster_distributed_lock', 'UPDATE') AS can_update;
   ```

   Both must be `true`; otherwise grant them, or set both strings to the lease's role. To skip the probe altogether,
   make the two strings identical.
3. **If they name two databases**, the most likely cause being a `ConnectionStrings:Dialer` that points elsewhere while
   `ConnectionStrings:Analytics` is unset, decide where the event store lives before you upgrade:
   - set `ConnectionStrings:Analytics` to the lease's database, which leaves the dialer where it is; or
   - set `ConnectionStrings:Cluster` to the event store's database, which moves the lease.

   Either way, the event store's existing rows stay in the database they were written to; the Api does not copy them.
   Choose the side that keeps the history you need, or copy the event-store tables yourself.

### There is no escape hatch for a split topology

No setting lets clustered voice keep the lease and the event store in two databases. Hosting the voice lease in the event
store's database, chosen by the operator, is the one option the design keeps for that case. **It is not shipped.** The
measured exposure was nil: no shipped artefact and no lab topology splits the two. If you need a split, tell us before
you upgrade instead of staying on the older release.

### Helm: `realtime.cluster.connectionStringName` is not read

`infra/k8s/helm/platform/values.yaml:240-248` documents `realtime.cluster.connectionStringName` as the
`ConnectionStrings__*` key Pro.Cluster reads for the lease. No template reads that value. The Realtime deployment sets
`ConnectionStrings__Cluster` inline from `api.postgres`, and the Api's `Program.cs` reads the key `"Cluster"` literally,
falling back to `"Postgres"`, as does the probe. Changing the value changes nothing. To move the Api's lease database,
set `ConnectionStrings__Cluster` on the Api deployment, and then apply the precondition above.

## After the upgrade: the runtime check

The startup probe cannot see a change after boot, such as a DNS change or a failover to another database. At runtime a
replica that believes it is the voice leader reports the `eventstore-subscriber` health check **Unhealthy**, on
`/health/voice`, with one critical log line naming the lease resource and the database, when its fence:

- finds **no lease row** for the resource in the event store's database (`NoRow`), at once; or
- keeps answering `NotOwner` or `Expired` for **longer than one lease renewal interval** (10 s by default).

A shorter refusal is the normal hand-over window in which a deposed leader has not yet noticed. It is only counted, as
`eventstore.events.skipped{reason="not_writer"}` on the meter `Verbara.Sdk.Pro.EventStore`. A count that keeps rising on a
replica that stays the leader is the same fault before the health check turns.

## Rolling back

Deploy the previous image. The probe and the fence go with it, and the previous build starts against the same databases
as before. The Pro event-store migration is additive; the previous build ignores its columns and table, so it does not
need to be undone. If you want it undone, the statements are in the migration's header in Verbara.Sdk.Pro.
