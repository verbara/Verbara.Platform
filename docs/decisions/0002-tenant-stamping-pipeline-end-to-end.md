# ADR-0002: Tenant stamping pipeline end-to-end

- **Status:** Accepted (policy locked 2026-04-25; execution gated to R5.2 ticket B.1 + audit sweep)
- **Date:** 2026-04-25
- **Deciders:** Harold Reina
- **Related:**
  - Post-ship R5.1 triage F.1 + B.1 + ADR placeholder (`docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`)
  - R5.1 implementation plan §"Known limitations" #1 (`docs/plans/completed/2026-04-22-r5.1-implementation-plan.md` line 1283)
  - Platform CHANGELOG v1.10.0 §Known limitations · Multi-tenant Pro.Analytics scope
  - SDK `Asterisk.Sdk.Cluster.Primitives.AsteriskSemanticConventions.Tenant` (the canonical attribute name source)

## Context

R5.1 R5.1 shipped the live queue metrics pipeline (`Asterisk.Sdk.Pro.Analytics.Live`) and wired Platform consumption via `/operations/queue-metrics`. **Both the writer and the read endpoint hardcode `tenantId=""`** because Platform registers `AddAsteriskAnalytics()` as a process-scope singleton with an empty `DefaultTenantId`. Today this works only because each deployed Platform instance serves a single logical tenant.

**Concrete evidence of the gap:**

- `Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Analytics/Live/LiveQueueSnapshotWriter.cs` writes `Metadata.TenantId = options.DefaultTenantId ?? ""`.
- `Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/Operations/QueueMetricsEndpoints.cs:66-67` queries `provider.GetSnapshotAsync(tenantId: "", queueName)`.
- The two sides are tightly coupled — flipping either alone yields zero data; both must flip atomically.
- Triage F.1 elevated this to a P0 R5.2 execution item (silent multi-tenant data-corruption risk) with explicit annotation in CHANGELOG.

**Broader pattern:** This is not unique to Pro.Analytics. It's the canonical example of a category — every Pro abstraction registered at process scope today (Pro.AgentAssist engine, Pro.CallAnalytics engine, Pro.EventStore subscriber, Pro.Cluster registry, Pro.Push backplane) faces the same question: *who stamps the TenantId on inflight events, snapshots, audit entries, traces, and metrics?* Some are correct today by coincidence (single-tenant deploys mask the bug); others are correct by design (Pro.Cluster scopes by NodeId, not TenantId, deliberately).

**Why a policy and not just one fix:** Fixing only Pro.Analytics in R5.2 closes the ticket but leaves the pattern ambiguous. Future packages, future endpoints, and future contributors will reintroduce the same shape unless the convention is written down.

## Decision

**Adopt a tenant stamping policy that applies to every state-bearing abstraction in Platform + Pro consumed by Platform. The R5.2 B.1 multi-tenant Pro.Analytics scope refactor is the first execution of this policy; the rest of the audit follows in R5.2/R5.3.**

### Policy: tenant stamping rules

Every abstraction that writes, reads, publishes, or persists state on behalf of a tenant **MUST**:

1. **Receive `ITenantContext`** via constructor injection (or via per-call parameter when the call is itself cross-tenant, e.g. an admin operation listing all tenants).
2. **Resolve the active `TenantId`** from `ITenantContext.Current.TenantId`. Never accept `string? tenantId = ""` as a fallback for "process default tenant" — that pattern is the root cause of B.1.
3. **Stamp `TenantId` on every emitted artifact**:
   - Postgres rows: column `tenant_id` populated from the resolver, **never empty string**. Database constraint `tenant_id <> ''` enforced at schema level for new tables.
   - Push events: `Metadata.TenantId` populated.
   - Activity tags: `AsteriskSemanticConventions.Tenant` (already adopted in 23 call-sites / 7 Pro packages per Pro v1.10.0-pro).
   - Meter dimensions: `tenant_id` tag on counter / histogram / gauge emissions where cardinality permits.
   - Audit entries: actor's tenant + target tenant captured separately (impersonation case).
4. **Filter reads by `TenantId`** in every query predicate. Cross-tenant aggregation requires explicit `[RequiresPlatformAdmin]` or equivalent gate + audit entry stamping the requester's tenant.
5. **Reject `TenantId == null` and `TenantId == ""`** as invalid input. Fast-fail with `ArgumentException("TenantId must be non-empty")`.

### Process-scope singletons: when allowed

A Pro abstraction MAY be registered as a process-scope singleton **only if** at least one of the following is true:

- **It manages cluster-level state** (e.g. `IClusterTransport`, `IClusterRegistry`, `IFailoverCoordinator`) — these are correctly scoped to NodeId, not TenantId.
- **It is a transport/backplane** (e.g. `IPushTransport`, `IRedisConnectionMultiplexer`) — these are infrastructure singletons; tenant stamping happens at the publish/subscribe layer, not the transport.
- **It is a stateless utility** (e.g. `IClock`, `IBackoffSchedule`) — no tenant context to stamp.

Any abstraction that holds in-memory caches keyed by tenant data, or that writes to tenant-scoped storage, **MUST NOT** be process-scope singleton. It must be either:

- **Scoped (`AddScoped`)** with `ITenantContext` resolved per request; or
- **Singleton with per-call `ITenantContext` resolution** via `IHttpContextAccessor` / explicit per-method parameter.

### R5.2 B.1 execution scope

The first concrete application of this policy:

1. **Pro.Analytics DI refactor:** `AddAsteriskAnalytics()` no longer auto-resolves `DefaultTenantId` from options. Instead, the builder injects `ITenantContext` (or fails fast in single-tenant deploys with explicit opt-in `WithSingleTenantMode("default")` builder method).
2. **`LiveQueueSnapshotWriter`:** consumes `LiveQueueStateEvent` which already carries `TenantId` in its envelope (R5.1 Task G shipped this). Writer stamps the row with `event.Metadata.TenantId`, never with a singleton default.
3. **`/operations/queue-metrics` endpoint:** resolves `ITenantContext.Current.TenantId` from request scope, queries `provider.GetSnapshotAsync(tenantId: ctx.TenantId, queueName)`.
4. **Postgres schema migration V006:** add constraint `CHECK (tenant_id <> '')` on `live_queue_snapshots`; add migration step "purge legacy rows with `tenant_id=''`" with rollback path.
5. **Audit sweep:** within R5.2 dev window, audit Pro.AgentAssist + Pro.CallAnalytics + Pro.EventStore + Pro.Push.SignalR for the same pattern. File issues for any other case found; treat them as R5.2 sub-items if S; defer to R5.3 if M+.

### Tenant stamping conventions table (canonical reference)

| Surface | Field name | Source |
|---|---|---|
| Postgres column | `tenant_id` (text, NOT NULL, CHECK <> '') | Schema migration |
| Push event metadata | `Metadata.TenantId` | `PushEventMetadata` (SDK 1.10.x) |
| Activity tag | `asterisk.tenant.id` | `AsteriskSemanticConventions.Tenant` (SDK 1.15.0) |
| Meter dimension | `tenant_id` | per-meter `KeyValuePair<string, object?>` |
| Audit entry | `actor.tenant_id` + `target.tenant_id` | `AuditEntry` schema |
| HTTP header (cross-service) | `X-Tenant-Id` | already present in PlatformHub auth |
| JWT claim | `tenant_id` | already present in identity issuer |

## Consequences

**Positivas:**
- Closes silent multi-tenant data-corruption risk on Pro.Analytics (B.1).
- Establishes single rule that future contributors apply uniformly — eliminates "but the other singleton works" reasoning.
- Database constraint `tenant_id <> ''` makes the bug detectable at schema level (insert rejected) rather than at query-result level (silent overlap).
- Aligns with SDK `AsteriskSemanticConventions.Tenant` adoption already in flight (7 Pro packages, 23 call-sites since Pro v1.10.0-pro).
- Brings R5.4 multi-tenant load-test scenario (S5.1 Track A) into a meaningful state — without this fix, the test cannot stress the multi-tenant path because it's not separated.

**Negativas:**
- **R5.2 dev cost ~3-4 days** for B.1 alone, plus ~1-2 days for the audit sweep. Within R5.2 envelope but tightens it.
- **Single-tenant deploys need explicit opt-in** via builder (`WithSingleTenantMode("default")`). Unscientific operators may find this annoying — mitigated by sensible default exception message ("call WithSingleTenantMode if you intend single-tenant").
- **Existing tenant-id="" rows** in production-staging databases need migration step. Rollback path required.
- **Pro.OpenTelemetry call-sites** without `tenant_id` meter tag today need amendment as part of audit. May add work to R5.3.

## Alternatives considered

- **Fix Pro.Analytics only, defer policy to later:** rejected — F.1 explicit reason. Same shape will recur in unaudited packages; cost of writing this once is lower than re-arguing per-package.
- **Refactor every Pro process-scope singleton to scoped immediately:** rejected — too disruptive in R5.2 envelope. Audit sweep scopes the work to confirmed cases; refactor lands incrementally.
- **Keep singleton + add `Func<ITenantContext>` parameter to every method call:** rejected — same effective scope as the chosen policy but with worse ergonomics; loses constructor injection clarity.
- **Database `tenant_id` defaults to `'__default__'` instead of NOT NULL CHECK:** rejected — sentinel values are the original sin that caused B.1. Empty string and magic strings are equivalent failure modes.
- **Multi-tenant only enforced in v2.0:** rejected — silent data-corruption risk is a minor-version-impacting bug, not breaking-change-impacting feature. Fix in R5.2.

## Migration guide (for B.1 ticket execution in R5.2)

1. Add builder method `AsteriskAnalyticsBuilder.WithSingleTenantMode(string defaultTenantId)`.
2. Existing `AddAsteriskAnalytics(opt => opt.DefaultTenantId = "")` callers in Platform `Program.cs` swap to `AddAsteriskAnalytics(...).WithSingleTenantMode("default")` if they're knowingly single-tenant; otherwise inject `ITenantContext`.
3. `LiveQueueSnapshotWriter` reads `event.Metadata.TenantId` — already present per R5.1. No event-shape change.
4. `QueueMetricsEndpoints.cs:66-67` becomes `provider.GetSnapshotAsync(httpContext.User.GetTenantId(), queueName)`.
5. Postgres migration V006: `ALTER TABLE pro_analytics.live_queue_snapshots ADD CONSTRAINT chk_tenant_id_nonempty CHECK (tenant_id <> ''); DELETE FROM pro_analytics.live_queue_snapshots WHERE tenant_id = '';` — wrap in transaction, log row count, document rollback.
6. Audit sweep: search `AddSingleton<I.*Engine>` + `AddSingleton<I.*Subscriber>` + `AddSingleton<I.*Provider>` across Pro packages registered in Platform.

## References

- B.1 ticket detail: `docs/plans/active/2026-04-25-r5.1-post-ship-triage.md` Table 2
- F.1 reclassification context: same triage doc, §"D-FORCE-3"
- SDK semantic conventions adoption (Pro v1.10.0-pro, 23 call-sites): `Asterisk.Sdk.Pro/CHANGELOG.md` v1.10.0-pro entry
- R5.1 limitation #1 source: `docs/plans/completed/2026-04-22-r5.1-implementation-plan.md` lines 1283-1289
- R5.4 Track A multi-tenant load tests (S5.1): `docs/plans/active/2026-04-22-r5-production-readiness-release-train.md` Release 4 section
