# Consolidate Platform DB Migrations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge 7 incremental Platform migration files (001-007) into a single `001_InitialSchema.sql` with the final schema.

**Architecture:** Read all 7 files, merge CREATE/ALTER/RENAME into unified CREATE TABLE statements, delete obsolete files, verify schema equivalence.

**Tech Stack:** PostgreSQL DDL, Docker Compose, .NET CLI tool

**Spec:** `docs/superpowers/specs/2026-03-29-consolidate-migrations-design.md`

---

### Task 1: Capture baseline schema dump

**Files:**
- None modified — verification only

- [ ] **Step 1: Start the demo Docker stack from scratch**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
docker compose -f docker/docker-compose.full.yml down -v 2>/dev/null; \
docker compose -f docker/docker-compose.full.yml up -d postgres
```

Wait for postgres healthy:

```bash
docker compose -f docker/docker-compose.full.yml exec postgres pg_isready -U asterisk
```

- [ ] **Step 2: Apply current migrations manually to get baseline**

```bash
for f in src/Asterisk.Platform.Storage.Postgres/Migrations/*.sql; do
  docker compose -f docker/docker-compose.full.yml exec -T postgres \
    psql -U asterisk -d asterisk -f /docker-entrypoint-initdb.d/$(basename "$f")
done
```

- [ ] **Step 3: Dump schema-only as baseline**

```bash
docker compose -f docker/docker-compose.full.yml exec postgres \
  pg_dump -U asterisk -d asterisk --schema-only --no-owner --no-privileges \
  | grep -v '^--' | grep -v '^$' | grep -v '^SET ' | grep -v '^SELECT ' \
  > /tmp/schema_before.sql
```

- [ ] **Step 4: Stop the stack**

```bash
docker compose -f docker/docker-compose.full.yml down -v
```

---

### Task 2: Write consolidated `001_InitialSchema.sql`

**Files:**
- Rewrite: `src/Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql`

- [ ] **Step 1: Rewrite 001_InitialSchema.sql with the full consolidated schema**

The file must contain all 32 tables with their final column definitions, all indexes, all constraints. Sections in order:

1. **Identity** — `users` (original 001 columns + 005 auth columns inline), `api_keys` (with `user_id`)
2. **Auth** — `refresh_tokens`, `auth_events`, `tenant_auth_config` (from 005)
3. **RBAC** — `permissions`, `role_templates`, `role_template_permissions`, `tenant_roles`, `tenant_role_permissions`, `user_roles` (from 006)
4. **Conversations** — `conversations`, `messages`, `contacts` (from 001)
5. **Queues** — `queue_configs` (was `queues` in 001, renamed in 007 — use final name directly), `agents` (with `extension` + `sip_password` from 004), `queue_memberships` (from 004)
6. **Channels** — `tenant_channel_configs` (from 001)
7. **Flows** — `flow_definitions`, `flow_executions` (from 001)
8. **Bot** — `bot_configurations` (from 001)
9. **Automation** — `automation_rules`, `scheduled_timers` (from 001), `automation_execution_logs` (from 003)
10. **KnowledgeBase** — `articles` (from 002)
11. **Teams & Cases** — `teams`, `cases`, `dispositions`, `wrap_up_records`, `service_accounts` (from 003)
12. **Surveys** — `surveys`, `survey_responses` (from 003)
13. **Audit** — `audit_entries` (from 003)
14. **Media** — `media_files` (from 003)

Key merge rules:
- `users`: CREATE TABLE with all columns from 001 + 005. Keep `role INTEGER NOT NULL` from 001. Add `password_hash TEXT`, `mfa_enabled BOOLEAN NOT NULL DEFAULT false`, `mfa_secret TEXT`, `mfa_recovery_codes TEXT[]`, `mfa_confirmed_at TIMESTAMPTZ`, `email_verified BOOLEAN NOT NULL DEFAULT false`, `failed_login_attempts INT NOT NULL DEFAULT 0`, `locked_until TIMESTAMPTZ`, `password_changed_at TIMESTAMPTZ`, `last_login_at TIMESTAMPTZ`, `auth_provider TEXT NOT NULL DEFAULT 'local'`, `external_id TEXT`.
- `api_keys`: Add `user_id TEXT` column in the CREATE TABLE.
- `agents`: Add `extension VARCHAR(20)` and `sip_password VARCHAR(80)` in the CREATE TABLE.
- `queue_configs`: Use name `queue_configs` directly instead of `queues`.
- All `IF NOT EXISTS` clauses preserved on CREATE TABLE and CREATE INDEX.

- [ ] **Step 2: Verify the file has all 32 tables**

```bash
grep -c 'CREATE TABLE IF NOT EXISTS' src/Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql
```

Expected: `32`

---

### Task 3: Delete obsolete migration files

**Files:**
- Delete: `src/Asterisk.Platform.Storage.Postgres/Migrations/002_Articles.sql`
- Delete: `src/Asterisk.Platform.Storage.Postgres/Migrations/003_RemainingStores.sql`
- Delete: `src/Asterisk.Platform.Storage.Postgres/Migrations/004_AsteriskRealtime.sql`
- Delete: `src/Asterisk.Platform.Storage.Postgres/Migrations/005_AuthEnterprise.sql`
- Delete: `src/Asterisk.Platform.Storage.Postgres/Migrations/006_RbacGranular.sql`
- Delete: `src/Asterisk.Platform.Storage.Postgres/Migrations/007_RenameQueuesToQueueConfigs.sql`

- [ ] **Step 1: Delete the 6 obsolete files**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
rm src/Asterisk.Platform.Storage.Postgres/Migrations/002_Articles.sql
rm src/Asterisk.Platform.Storage.Postgres/Migrations/003_RemainingStores.sql
rm src/Asterisk.Platform.Storage.Postgres/Migrations/004_AsteriskRealtime.sql
rm src/Asterisk.Platform.Storage.Postgres/Migrations/005_AuthEnterprise.sql
rm src/Asterisk.Platform.Storage.Postgres/Migrations/006_RbacGranular.sql
rm src/Asterisk.Platform.Storage.Postgres/Migrations/007_RenameQueuesToQueueConfigs.sql
```

- [ ] **Step 2: Verify only 001 remains**

```bash
ls src/Asterisk.Platform.Storage.Postgres/Migrations/
```

Expected: only `001_InitialSchema.sql`

---

### Task 4: Update `008_pro_tables.sql` header comment

**Files:**
- Modify: `docker/demo/sql/008_pro_tables.sql:1-14`

- [ ] **Step 1: Update the header comment**

Change line 4:
```
-- Runs during Postgres init (docker-entrypoint-initdb.d) AFTER Platform
-- migrations (001-006) and BEFORE demo seed data (010).
```
To:
```
-- Runs during Postgres init (docker-entrypoint-initdb.d) AFTER Platform
-- migration (001) and BEFORE demo seed data (010).
```

---

### Task 5: Verify schema equivalence

**Files:**
- None modified — verification only

- [ ] **Step 1: Start fresh stack with consolidated migration**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
docker compose -f docker/docker-compose.full.yml down -v 2>/dev/null; \
docker compose -f docker/docker-compose.full.yml up -d postgres
```

Wait for healthy, then:

```bash
docker compose -f docker/docker-compose.full.yml exec postgres pg_isready -U asterisk
```

- [ ] **Step 2: Dump the new schema**

```bash
docker compose -f docker/docker-compose.full.yml exec postgres \
  pg_dump -U asterisk -d asterisk --schema-only --no-owner --no-privileges \
  | grep -v '^--' | grep -v '^$' | grep -v '^SET ' | grep -v '^SELECT ' \
  > /tmp/schema_after.sql
```

- [ ] **Step 3: Diff the two schemas**

```bash
diff /tmp/schema_before.sql /tmp/schema_after.sql
```

Expected: Only difference should be `queue_configs` vs `queues` (since before had the rename, after creates directly as `queue_configs`). The column definitions, indexes, and constraints must be identical.

- [ ] **Step 4: Stop the stack**

```bash
docker compose -f docker/docker-compose.full.yml down -v
```

---

### Task 6: Run tests and CLI migrate

**Files:**
- None modified — verification only

- [ ] **Step 1: Build the solution**

```bash
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: Run all tests**

```bash
dotnet test Asterisk.Platform.slnx -v q
```

Expected: All tests pass (same count as before, the 22 pre-existing API failures unchanged).

- [ ] **Step 3: Test CLI migrate against fresh DB**

```bash
docker compose -f docker/docker-compose.full.yml up -d postgres
# wait for healthy
docker compose -f docker/docker-compose.full.yml exec postgres pg_isready -U asterisk

dotnet run --project tools/Asterisk.Platform.Cli -- migrate \
  --connection "Host=localhost;Port=5432;Database=asterisk;Username=asterisk;Password=asterisk"
```

Expected output:
```
Asterisk.Platform — Database Migration
======================================
  RUN   001_InitialSchema.sql ... OK

1 migration(s) applied.
```

- [ ] **Step 4: Test CLI doctor**

```bash
dotnet run --project tools/Asterisk.Platform.Cli -- doctor \
  --connection "Host=localhost;Port=5432;Database=asterisk;Username=asterisk;Password=asterisk"
```

Expected: All 32 tables found, health OK.

- [ ] **Step 5: Stop the stack**

```bash
docker compose -f docker/docker-compose.full.yml down -v
```

---

### Task 7: Commit

**Files:**
- All changes from Tasks 2-4

- [ ] **Step 1: Stage and commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Migrations/
git add docker/demo/sql/008_pro_tables.sql
git commit -m "refactor(storage): consolidate 7 migrations into single 001_InitialSchema

Merge migrations 001-007 into unified initial schema. No production
deployments exist so migration history is unnecessary. Creates all 32
Platform tables with final column definitions directly."
```
