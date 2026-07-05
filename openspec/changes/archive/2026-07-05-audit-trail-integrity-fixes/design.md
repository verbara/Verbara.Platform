# Design — audit-trail-integrity-fixes

Backlog change: design decisions are sketched here and finalized at apply time (grounding first).

- **Atomicity (fix 1):** prefer a single Postgres transaction spanning the correction insert and the
  audit insert (both stores already sit on `Verbara.Sdk.Data.Npgsql`); a transactional outbox is the
  fallback only if the stores cannot share a connection/transaction seam. InMemory store mirrors
  with a lock-scoped compound operation.
- **Preview count (fix 2):** add a count query to the audit store (`CountByActorAsync` /
  by-target-user as appropriate — decide at grounding which linkage the purge actually deletes by),
  wire into `PreviewUserPurgeAsync`. `COUNT(*)` → `ExecuteScalarAsync<long?>(...) ?? 0L` per the
  workspace Npgsql conventions.
- **Actor resolution (fix 3):** reuse the canonical resolution shipped with the v2.14.1
  impersonation claim-order fix (the same precedence the rate limiter uses) — extract it to a shared
  helper if it is currently inline, then route every `RecordAudit` call-site through it. Do NOT
  invent a second resolution order.
- **Hash versioning (fix 4):** prefix the hash input with a scheme discriminator (`v2|`) and store
  the scheme (either encoded in the hash column prefix or a new smallint column — decide at
  grounding against `PostgresAuditStore` schema migration cost). Old rows verify under v1 (current
  field set); new rows under v2 (v1 fields + `RetainUntil`).
- **Constraints:** Native AOT (no reflection), TreatWarningsAsErrors, `Method_ShouldExpected_WhenCondition`
  test naming, `test-determinism` fences, live-DB Postgres tests for every fix (InMemory-only proved
  insufficient in this exact area — ADR-0034 lesson).
- **References:** Platform/ADR-0034 (autonomous disposition + audit trail), Platform/ADR-0022 (AOT),
  the v2.14.1 claim-order fix (PR #78).
