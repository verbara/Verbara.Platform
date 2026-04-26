# ADR-0006: Audit entries schema normalization

- **Status:** Accepted (executed during R5.3 Phase 0)
- **Date:** 2026-04-26
- **Deciders:** Harold Reina
- **Related:**
  - ADR-0002 `tenant-stamping-pipeline-end-to-end` (parent policy — every audit entry MUST stamp tenant)
  - ADR-0004 `tenant-stamping-execution-conventions` (companion — per-package conventions)
  - R5.3 spec §"D-FORCE-2" (`docs/plans/active/2026-04-26-r5.3-admin-completeness-r4-closure.md`)
  - Post-R5.2 deep audit findings (Agent 2 + Agent 4 cross-repo) — `AuditEntry.{Category, Severity, ActorType, Changes}` silent-loss bug
  - Audit Log Viewer (R5.2 PB.x shipped) — UI dropdown filters by `severity` / `category` without backing index

## Context

The `AuditEntry` C# model in `Asterisk.Platform.Audit/AuditEntry.cs` carries five canonical fields beyond the basic identity columns:

- `Category` (string, default `"config"`) — e.g. `auth`, `billing`, `tenant`, `security`, `impersonation`, `retention`, `data`
- `Severity` (string, default `"info"`) — e.g. `info`, `warn`, `error`, `critical`
- `ActorType` (string, default `"system"`) — e.g. `user`, `system`, `impersonator`, `service-account`
- `Changes` (`AuditChanges` record) — `Before` + `After` snapshots for tamper-evident diff
- `IntegrityHash` (string?) — SHA-256 of canonicalized payload for forensic chain

The Postgres writer at `Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs:23-37` **persists none of these fields**:

```csharp
const string sql = @"
    INSERT INTO audit_entries
        (entry_id, tenant_id, action, entity_type, entity_id,
         performed_by, details, occurred_at, impersonator_id)
    VALUES (...)";
```

The actual schema (per `Migrations/001_InitialSchema.sql` + `011_OnboardingAudit.sql`) is:

```
audit_entries (entry_id, tenant_id, action, entity_type, entity_id,
               performed_by, details JSONB, occurred_at, impersonator_id)
```

Result: `Category`, `Severity`, `ActorType`, `Changes`, `IntegrityHash` are **silently lost or buried** in the `details` JSONB blob without contract. Audit Log Viewer (shipped in R5.2 PB.x) renders dropdown filters for `severity` and `category`, but the backend has no index — and in fact no column — to satisfy them efficiently. The query plan today is a sequential scan + JSONB extraction per row.

**Concrete evidence:**

- `AuditEntry.cs` defines `Category`, `Severity`, `ActorType`, `Changes`, `IntegrityHash` as first-class properties.
- `PostgresAuditStore.cs:23-37` SQL omits all five from INSERT.
- `audit_entries` schema lacks columns `category`, `severity`, `actor_type`, `before_json`, `after_json`, `integrity_hash`.
- Audit Log Viewer (`Asterisk.Platform.Web/src/admin/audit/audit-viewer-page.tsx`) renders `<DropdownMenu>` filter for severity + category; backend response lacks these fields, so frontend either ignores filter or pulls all rows + filters client-side.
- Audit drawer "Before / After" tab cannot render structured diff because data isn't persisted.

**Why a schema migration and not JSONB-only:** A JSONB-only path (GIN index on `details->>'severity'`, etc.) is technically possible, but: (a) query plan is worse than a typed B-tree index on `(tenant_id, severity, occurred_at)`; (b) audit viewer code becomes more complex (JSONB extraction in every render); (c) `IntegrityHash` is a tamper-evidence column — burying it in a JSONB blob defeats the purpose of separable verification; (d) compliance auditors expect typed columns for audit trail integrity claims (SOC 2 ask).

## Decision

**Promote `Category`, `Severity`, `ActorType`, `BeforeJson`, `AfterJson`, `IntegrityHash` to first-class typed columns in `audit_entries` via migration `V012_AuditEntriesNormalize.sql`. Add CHECK constraints + indexes per `(tenant_id, severity, occurred_at)` and `(tenant_id, category, occurred_at)`. Update `PostgresAuditStore` writer + reader to populate and hydrate the new columns. Backwards compat: legacy `Metadata` dict continues to serialize into `details` JSONB.**

### Migration V012 shape

3-stage atomic transaction:

1. **Add nullable columns** — `ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS category TEXT;` (5 more — `severity`, `actor_type`, `before_json`, `after_json`, `integrity_hash`).
2. **Backfill from `details` JSONB blob** (best-effort) — `UPDATE ... SET category = COALESCE(details->>'category', 'config'), ...` plus `RAISE NOTICE` audit of how many rows were backfilled vs defaulted.
3. **NOT NULL + CHECK constraints + indexes:**
   - `ALTER COLUMN category SET NOT NULL DEFAULT 'config'` (same for `severity` → `info`, `actor_type` → `system`)
   - `CHECK (severity IN ('info','warn','error','critical'))`
   - `CHECK (category IN ('auth','billing','config','tenant','security','impersonation','retention','data'))`
   - `CHECK (actor_type IN ('user','system','impersonator','service-account'))`
   - `CREATE INDEX idx_audit_severity ON audit_entries (tenant_id, severity, occurred_at DESC)`
   - `CREATE INDEX idx_audit_category ON audit_entries (tenant_id, category, occurred_at DESC)`

### Writer update

```csharp
const string sql = @"
    INSERT INTO audit_entries
        (entry_id, tenant_id, action, entity_type, entity_id,
         performed_by, details, occurred_at, impersonator_id,
         category, severity, actor_type, before_json, after_json, integrity_hash)
    VALUES (@EntryId, @TenantId, @Action, @EntityType, @EntityId,
            @PerformedBy, @Details::jsonb, @OccurredAt, @ImpersonatorId,
            @Category, @Severity, @ActorType, @BeforeJson::jsonb, @AfterJson::jsonb, @IntegrityHash)";

var parameters = new {
    // ... existing 9 fields ...
    Category      = entry.Category ?? "config",
    Severity      = entry.Severity ?? "info",
    ActorType     = entry.ActorType ?? "system",
    BeforeJson    = entry.Changes?.Before is not null
                    ? JsonSerializer.Serialize(entry.Changes.Before, AuditJsonContext.Default.JsonElement)
                    : null,
    AfterJson     = entry.Changes?.After is not null
                    ? JsonSerializer.Serialize(entry.Changes.After, AuditJsonContext.Default.JsonElement)
                    : null,
    IntegrityHash = entry.IntegrityHash,
};
```

### Reader update

`PostgresAuditStore.QueryAsync` hydrates `AuditEntry.Changes` from `before_json` + `after_json` columns directly; maps `category` / `severity` / `actor_type` to typed properties; `Metadata` dictionary remains in `details` JSONB blob (backwards compat preserved).

### Concurrent-write safety

- **Atomic transaction safe** for deploys with <100k audit rows (typical for current single-tenant production). `ALTER TABLE` takes `ACCESS EXCLUSIVE` lock; UPDATE inside same transaction completes within the same lock window.
- **For deploys >10M rows:** documented batch pattern in `docs/operations/migrations.md` (R5.3 Phase C output) — Stage 1 `ADD COLUMN` in separate transaction (no prolonged lock in Postgres ≥11), Stage 2 `UPDATE` in 10k batches via `LIMIT` loop, Stage 3 NOT NULL + CHECK + INDEX in maintenance window.

## Decision space

**(a) Normalize (chosen):** typed columns + CHECK constraints + indexes + writer/reader update. Migration V012.

**(b) JSONB-only path:** Add GIN indexes on `details->>'severity'` / `details->>'category'`. No schema change. Rejected because:
- Query plan is worse than B-tree on typed column (GIN GIN extraction + filter vs hash lookup).
- Audit viewer code complexity rises (JSON extraction in every render path).
- `IntegrityHash` belongs in a separable column for tamper-evidence verification independence.
- Compliance auditors (SOC 2, ISO 27001) expect typed columns; JSONB-everything weakens audit narrative.

**(c) Acceptable status quo:** Defer normalization to R6 / 2.0 break window. Rejected because:
- Audit Viewer (R5.2 ship) already exposes filter UX that the backend cannot satisfy efficiently — present a regression to users.
- Each row of `audit_entries` insert today silently loses 5 structured fields. The longer this runs, the larger the data archeology problem at remediation time.
- 1 migration now vs migration + index rebuild later when the table is 100M+ rows is asymmetric cost.

## Consequences

**Positivas:**
- 10-100x query performance for severity / category filters (B-tree index lookup vs JSONB sequential extraction).
- SOC 2 audit log integrity story strengthened (typed columns + integrity hash separable).
- Audit Log Viewer drawer "Before / After" tab renders structured diff directly from 2 columnas without frontend JSON deep-parse.
- Backwards compat preserved: legacy `Metadata` dict continues in `details` JSONB blob; readers without typed columns still get all data.
- Future audit features (e.g., severity-based alerting, category-based retention windows) become trivially implementable — typed columns are filterable, JSONB blobs are not.

**Negativas:**
- +50-200 bytes per row storage (depends on before/after blob sizes). Acceptable for audit table cardinality.
- +2-3% insert latency per row (negligible for audit workload — already async via `IAuditService.WriteAsync`).
- Migration window required: atomic safe <100k rows; documented batch pattern for >10M rows. Production deployments under those bounds will see <1s downtime; large deployments need scheduled maintenance.
- `details` JSONB blob still carries `Metadata` dict — query consumers that filter on dict keys still pay JSONB cost. Not addressed in this ADR (separate decision; defer to v2.0 break window if metadata-by-key-filter becomes a bottleneck).

## Alternatives considered

See **Decision space** above.

## References

- ADR-0002 `tenant-stamping-pipeline-end-to-end.md`
- ADR-0004 `tenant-stamping-execution-conventions.md`
- R5.3 spec: `docs/plans/active/2026-04-26-r5.3-admin-completeness-r4-closure.md` §"D-FORCE-2"
- Code: `Asterisk.Platform.Audit/AuditEntry.cs`
- Code: `Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs:23-37`
- Schema: `Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql`
- Schema: `Asterisk.Platform.Storage.Postgres/Migrations/011_OnboardingAudit.sql`
- R5.3 execution plan task A.1: `docs/plans/active/2026-04-26-r5.3-execution-plan.md`
