# Consolidate Platform DB Migrations into Single Schema

**Date:** 2026-03-29
**Status:** Approved
**Scope:** Platform migrations only (001-007). Pro packages untouched.

## Problem

Seven incremental migration files (001-007) accumulated during development. Since no production deployment exists, these carry unnecessary historical baggage:

- `001` creates `queues` table, `007` renames it to `queue_configs`
- `001` creates `users` without auth fields, `005` ALTERs 12 columns onto it
- `001` creates `agents` without SIP fields, `004` ALTERs 2 columns onto it
- `001` creates `api_keys` without `user_id`, `005` ALTERs it on

New developer onboarding requires reading 7 files and mentally replaying history to understand the current schema.

## Solution

Merge all 7 migrations into a single `001_InitialSchema.sql` that creates the final schema directly. Delete files 002-007.

## Consolidated Schema (32 tables)

### Section order in file

| Section | Tables | Original Source |
|---------|--------|-----------------|
| Identity | `users` (with auth fields), `api_keys` (with user_id) | 001 + 005 |
| Auth | `refresh_tokens`, `auth_events`, `tenant_auth_config` | 005 |
| RBAC | `permissions`, `role_templates`, `role_template_permissions`, `tenant_roles`, `tenant_role_permissions`, `user_roles` | 006 |
| Conversations | `conversations`, `messages`, `contacts` | 001 |
| Queues | `queue_configs` (direct name), `agents` (with SIP fields), `queue_memberships` | 001 + 004 + 007 |
| Channels | `tenant_channel_configs` | 001 |
| Flows | `flow_definitions`, `flow_executions` | 001 |
| Bot | `bot_configurations` | 001 |
| Automation | `automation_rules`, `scheduled_timers`, `automation_execution_logs` | 001 + 003 |
| KnowledgeBase | `articles` | 002 |
| Teams & Cases | `teams`, `cases`, `dispositions`, `wrap_up_records`, `service_accounts` | 003 |
| Surveys | `surveys`, `survey_responses` | 003 |
| Audit | `audit_entries` | 003 |
| Media | `media_files` | 003 |

### Key merges

1. **`users` table** — created with all 12 auth columns (`password_hash`, `mfa_enabled`, `mfa_secret`, `mfa_recovery_codes`, `mfa_confirmed_at`, `email_verified`, `failed_login_attempts`, `locked_until`, `password_changed_at`, `last_login_at`, `auth_provider`, `external_id`) inline in the CREATE TABLE
2. **`agents` table** — created with `extension` and `sip_password` columns inline
3. **`api_keys` table** — created with `user_id` column inline
4. **`queue_configs`** — created directly with final name (no rename step)
5. **All tables from 002, 003, 006** — moved into appropriate sections

## Affected files

| File | Action |
|------|--------|
| `src/.../Migrations/001_InitialSchema.sql` | Rewrite with consolidated schema |
| `src/.../Migrations/002_Articles.sql` | Delete |
| `src/.../Migrations/003_RemainingStores.sql` | Delete |
| `src/.../Migrations/004_AsteriskRealtime.sql` | Delete |
| `src/.../Migrations/005_AuthEnterprise.sql` | Delete |
| `src/.../Migrations/006_RbacGranular.sql` | Delete |
| `src/.../Migrations/007_RenameQueuesToQueueConfigs.sql` | Delete |
| `docker/demo/sql/008_pro_tables.sql` | Update header comment (001-006 -> 001) |

## What does NOT change

- Table names, columns, types, indexes, PKs, FKs (schema-identical output)
- Application code (stores, endpoints, DI registration)
- Pro packages and their `EnsureSchemaAsync()` mechanism
- RBAC seeders (`PermissionSeeder`, `RoleTemplateSeeder`, `RbacMigrationSeeder`)
- CLI tool code (`tools/Verbara.Platform.Cli/Program.cs`)
- Docker compose files
- Tests

## Verification

1. Rebuild Docker demo stack from scratch (`docker compose down -v && docker compose up`)
2. Compare `pg_dump --schema-only` before and after — must be identical
3. Run `dotnet test` — all tests must pass
4. Run CLI `migrate` against fresh DB — single migration applied
5. Verify RBAC seeding completes successfully

## Risks

| Risk | Probability | Mitigation |
|------|------------|------------|
| Missing column/index in consolidation | Low | Schema diff before/after |
| Docker demo init breaks | Low | Full stack rebuild test |
| CLI migrate breaks | None | Code reads `*.sql` from directory, finds 1 file |
| Future migration numbering confusion | None | Next migration will be `002_*.sql` |

## Benefits

1. **Single source of truth** — one file describes the entire Platform schema
2. **Clean baseline** — future migrations (002+) represent real incremental changes
3. **Faster onboarding** — read one file instead of replaying 7
4. **No dead steps** — no create-then-rename, no create-then-alter patterns
5. **Smaller Docker init** — one file, one transaction
