# ADR-0003: DataProtection key persistence strategy — DB-backed default with file-system fallback

- **Status:** Accepted (policy locked 2026-04-25; execution gated to R5.2 ticket B.6)
- **Date:** 2026-04-25
- **Deciders:** Harold Reina
- **Related:**
  - Post-ship R5.1 triage Limitation #6 + B.6 ticket (`docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`)
  - R5.1 implementation plan §"Known limitations" #6 (`docs/plans/completed/2026-04-22-r5.1-implementation-plan.md` line 1294)
  - Platform CHANGELOG v1.10.0 §Known limitations · DataProtection keyring persistence in Docker
  - Platform v1.9.2 hardening — `IDataProtectionProvider` adoption for JWT key wrap (precedent)
  - `docs/operations/agentassist-setup.md` (the file that documents this gap today)

## Context

`Asterisk.Sdk.Pro.AgentAssist.DependencyInjection.AgentAssistCredentialsProtector` uses ASP.NET Core's default DataProtection keyring at `/root/.aspnet/DataProtection-Keys`. Inside a Docker container this directory is **ephemeral** — when the container is recreated (image redeploy, `docker compose down/up`, host reboot, kube pod evict), all DataProtection keys are lost. **All credentials encrypted with those keys become unrecoverable.**

**Concrete impact:**

- `AgentAssistCredentialsProtector` wraps STT/TTS provider API keys (Deepgram, Whisper, Azure, Google, ElevenLabs) before persisting in `agent_assist.feature_toggle` table. Container recreation means tenants must re-enter every provider key.
- Same risk applies to any future component that uses default `IDataProtectionProvider` without explicit persistence — the JWT key wrap pattern from v1.9.2 is similarly vulnerable if not mounted to a stable path or backed by a persistent store.

**Triage classification:** Limitation #6 — R5.2 ops polish ticket B.6. CHANGELOG documents the gap and references this ADR slot. Operator runbook entry needed but **insufficient on its own**: a runbook step that says "remember to mount this volume" is exactly the kind of footgun real production deployments forget. Code-level default needs to be safe.

**Decision space:**

ASP.NET Core DataProtection supports four persistence backends:

1. **In-memory** (default): keys regenerated on each process start. Bad for any encrypted-at-rest data. Currently in use by accident.
2. **`PersistKeysToFileSystem(DirectoryInfo)`**: keys written to specified directory. Works if directory is mounted from a stable volume. Single-node friendly.
3. **`PersistKeysToDbContext(DbContext)`**: keys written to a Postgres table (or any EF-supported store). Cluster-HA friendly — multiple Platform nodes share the same keyring.
4. **`PersistKeysToAzureBlobStorage` / `PersistKeysToAwsSystemsManager` / vault providers**: cloud-managed storage. Good for SaaS, overkill for single-region.

**Why a default matters:** the current "no explicit persistence" default is silent danger. Operators who don't read the runbook get a working dev environment that loses all credentials on first redeploy in production. That's a footgun this ADR closes by making the safe path the default, not the runbook-only path.

## Decision

**Default to DB-backed persistence (`PersistKeysToDbContext`) using a dedicated `DataProtectionKey` table in the Platform Postgres database. Provide explicit opt-in builder methods for file-system mode (single-node deploys) and for "I know what I'm doing" no-persistence mode (test/CI only).**

### API shape (R5.2 B.6 execution)

```csharp
// Default — DB-backed via Platform's primary Postgres connection.
// Recommended for cluster-HA and multi-node deploys.
services.AddPlatformDataProtection(opt =>
{
    opt.ApplicationName = "Asterisk.Platform";
    // Defaults: DB-backed via Platform DbContext, key rotation 90d, automatic.
});

// Opt-in: file-system mode for single-node.
services.AddPlatformDataProtection(opt =>
{
    opt.ApplicationName = "Asterisk.Platform";
    opt.UseFileSystem("/var/lib/asterisk-platform/dataprotection-keys");
});

// Opt-in: no persistence (test/CI only — explicit warning logged).
services.AddPlatformDataProtection(opt =>
{
    opt.ApplicationName = "Asterisk.Platform";
    opt.UseEphemeralKeysForTesting();
});
```

The default must **fail fast** if the configured DbContext is not registered or cannot reach the database — never silently fall back to ephemeral. Eliminates the current footgun.

### Schema

New table in Platform's primary Postgres schema (created via existing migration mechanism — same path as JWT key storage from v1.9.2):

```
asterisk_platform.data_protection_keys
  id                   bigserial PRIMARY KEY
  friendly_name        text NOT NULL
  xml                  text NOT NULL          -- ASP.NET DataProtection serializes as XML
  created_at           timestamptz NOT NULL
  activates_at         timestamptz NOT NULL
  expires_at           timestamptz NOT NULL
  is_revoked           boolean NOT NULL DEFAULT false
  revocation_reason    text NULL
  -- index on activates_at for hot-key lookup
```

Schema is **not tenant-scoped** — DataProtection keys are infrastructure, owned by the Platform installation. Per-tenant credential isolation happens at the encrypted-payload level (each tenant's encrypted credentials are stored in tenant-scoped tables; the DataProtection keyring is the single Platform-wide unwrap key).

### Cluster-HA semantics

Multiple Platform API replicas pointing at the same Postgres database **automatically share keys** via the EF Core context. No coordination required beyond standard Postgres concurrency. Key rotation events propagate via the next-load refresh window (default 24h cache; tunable).

### Migration path for existing deploys

1. **Fresh installs:** new default applies; first key generated automatically on first startup, persisted to DB.
2. **Upgraded deploys with existing ephemeral keyring:** **the existing in-memory keys are lost**. Operators must re-enter encrypted credentials (AgentAssist provider keys) once after upgrade. This is a **one-time migration tax**, documented in v1.11 release runbook (R5.2 ship). The alternative — preserving ephemeral keys — is impossible because they were ephemeral by definition.
3. **Existing single-node deploys with `PersistKeysToFileSystem` already configured ad hoc:** they migrate to DB-backed by switching the builder call. Old file-system keys can be drained via the standard DataProtection key revocation procedure or simply orphaned (low risk; only AgentAssist credentials use this keyring today, and re-entry is acceptable).

### Key rotation policy (defaults)

- **Default key lifetime:** 90 days (ASP.NET Core default).
- **Cache duration:** 24 hours (default; refreshes from DB to pick up new keys generated by other replicas).
- **Manual rotation:** admin endpoint `POST /management/security/dataprotection/rotate-keys` (gated by `security.keyring.rotate` permission, audit entry `keyring.rotated`).
- **Automatic rotation on schedule:** disabled by default in v1.11 (manual rotation only). Re-evaluate in R5.4 S5.9 (JWT multi-key rotation completion ADR may converge with this).

### Failure modes

- **Database unreachable on startup:** Platform fails to start with explicit message naming this ADR. Aligns with existing pattern (Identity Postgres registry also fails fast).
- **Database unreachable during runtime:** existing in-process key cache continues serving for cache-duration window. No new keys generated; ad-hoc admin rotations queued or rejected (TBD by impl). Default behavior matches ASP.NET Core conventions.
- **Encrypted payload encrypted with revoked key:** unwrap fails → component-specific behavior (AgentAssist marks credentials invalid + logs + audit; future components must define).

## Consequences

**Positivas:**
- **Default safe path** — operators don't need to read runbook to avoid credential loss on container recreation.
- **Cluster-HA out of the box** — multi-replica Platform shares keys without external state coordination.
- **Aligns with v1.9.2 JWT key wrap precedent** — DB-backed persistence is already the pattern for JWT signing keys; this extends the same pattern to DataProtection keyring.
- **Auditable** — key generation/rotation/revocation events emit audit entries.
- **No new infrastructure dependency** — uses existing Platform Postgres database.
- **Fail-fast misconfiguration** — unreachable DB at startup yields actionable error, not silent ephemeral mode.

**Negativas:**
- **Postgres becomes more critical** — losing the DataProtection keys table means losing access to all encrypted-at-rest credentials. Mitigated by R5.4 S5.8 backup/DR runbook, which now MUST cover this table.
- **Slight startup latency** — DataProtection initialization adds a DB query. Negligible (~10ms typical).
- **One-time migration tax for existing deploys** — operators re-enter AgentAssist provider credentials once after upgrading to v1.11. Documented in release runbook.
- **DataProtection table is not tenant-scoped** — operators with strict per-tenant data residency requirements may need to evaluate. Mitigated: this is a Platform-installation-wide concern; per-tenant isolation belongs at the encrypted-payload level, not the keyring level.

## Alternatives considered

- **Default to `PersistKeysToFileSystem` + mounted volume** (option (a) from triage): rejected as default — operators forget to mount, footgun returns. Acceptable as opt-in for single-node deploys via explicit `UseFileSystem(path)` builder method.
- **Keep ephemeral default + require explicit opt-in to persistence**: rejected — the current state is exactly this, and it has produced the v1.10.0 limitation #6. The default needs to be the safe path.
- **Cloud KMS integration (AWS Secrets Manager / Azure Key Vault) as default**: rejected — adds external dependency for self-hosted deploys; cloud KMS is a SaaS-tier concern and belongs in Platform 2.0 with Stripe + multi-region foundation.
- **Custom XML-on-disk format keyed to tenant_id**: rejected — reinvents what ASP.NET Core DataProtection already provides correctly; multi-tenancy belongs at the encrypted-payload level, not the keyring layer.
- **Defer entire decision to R5.4 security sub-track**: rejected — limitation #6 is in current shipped CHANGELOG; deferring leaves customers vulnerable for ~3 sem of additional QA gap. R5.2 B.6 is the right place.

## Migration guide (R5.2 B.6 execution)

1. **New schema migration** (Platform): `XX_add_data_protection_keys_table.sql` with the table definition above + index on `activates_at`.
2. **New DI extension** in Platform.Api (or shared Platform.Core): `services.AddPlatformDataProtection(opt => ...)` per the API shape above.
3. **Replace existing `services.AddDataProtection()` call site** in `Program.cs` with the new extension. Fail-fast if DbContext not registered.
4. **AgentAssistCredentialsProtector** now receives `IDataProtectionProvider` configured by the new extension — no API change to the protector itself; consumers see no behavioral diff except keys persist.
5. **Operations runbook update**: `docs/operations/agentassist-setup.md` — remove the warning, add note "DataProtection keyring is now DB-backed by default; backup includes the `data_protection_keys` table per `docs/operations/backup-disaster-recovery.md` (R5.4)".
6. **Release runbook (v1.11):** explicit step "after upgrade, re-enter AgentAssist provider credentials in admin UI; this is a one-time migration".
7. **Tests:**
   - Unit: ephemeral mode for unit tests (existing pattern preserved).
   - Integration (Testcontainers): DB-backed mode verified end-to-end. Key rotation cycle tested.
   - E2E: AgentAssist credential round-trip after container recreate (Playwright + docker-compose teardown/restart).

## References

- B.6 ticket detail: `docs/plans/active/2026-04-25-r5.1-post-ship-triage.md` Table 2
- Limitation #6 source: `docs/plans/completed/2026-04-22-r5.1-implementation-plan.md` line 1294
- ASP.NET Core DataProtection docs: https://learn.microsoft.com/aspnet/core/security/data-protection/
- v1.9.2 JWT key wrap precedent: `docs/plans/completed/2026-04-21-r3c-platform-v1.9.2-hardening-follow-through.md`
- R5.4 backup/DR runbook (where this table must be covered): `docs/plans/active/2026-04-22-r5-production-readiness-release-train.md` §"S5.8 Backup/DR runbook"
