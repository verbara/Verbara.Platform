# Phase D — Day 1 empirical findings + strategic pivot (2026-05-19)

Companion to [Day 0 notes](2026-05-19-day-0-notes.md). Closes the spike attempt on `feat/phase-d-dapper-aot-spike` (branch abandoned post-evidence-capture) and records the pivot to **Option O — Dapper.Stubs** path.

## What Day 1 attempted

Per the original plan, the Canary A spike on `Verbara.Sdk.Sessions.Postgres`:
- Branch `feat/phase-d-dapper-aot-spike` created on Verbara.Sdk
- `Dapper.AOT 1.0.52` added to `Directory.Packages.props`
- `<PackageReference Include="Dapper.AOT" PrivateAssets="all" />` + `<InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>` in the storage csproj
- `[module: DapperAot]` in new `AssemblyInfo.cs`
- Build: clean (0 warnings, 0 errors, 10s)
- **Generated interceptor files: 0** — first red flag

## R10 — empirical confirmation: Dapper.AOT does not intercept `CommandDefinition`-wrapped overloads

A/B experiment on `PostgresSessionStore.cs:147-148`:

| Call shape | Interceptor emitted in `obj/Debug/generated/Dapper.AOT.Analyzers/…/*.generated.cs` |
|---|---|
| `conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(_getByIdSql, new { id = sessionId }, cancellationToken: ct))` | ❌ NO |
| `conn.QuerySingleOrDefaultAsync<string?>(_getByIdSql, new { id = sessionId })` | ✅ YES |

When the simple overload is used, Dapper.AOT generates an interceptor with signature:
```csharp
QuerySingleOrDefaultAsync0(IDbConnection cnn, string sql, object? param,
    IDbTransaction? transaction, int? commandTimeout, CommandType? commandType)
```
— note **no `CancellationToken` parameter**.

R10 is corroborated by [DapperLib/DapperAOT issue #153 (open since 2025-05-04)](https://github.com/DapperLib/DapperAOT/issues/153) — "feat: support Dapper overloads with CommandDefinition" — a PR adding the missing intercept targets that has not been merged for 12+ months.

## R11 — empirical confirmation: DAP045 canonical CT-in-params pattern emits broken C#

A/B experiment swapping the params shape from `new { id }` to `new { id, cancellationToken = ct }`:

Generated `GetCancellationToken(object? args)` override (`obj/Debug/generated/.../Verbara.Sdk.Sessions.Postgres.generated.cs:60-74`):

```csharp
public override CancellationToken GetCancellationToken(object? args)
{
    var typed = Cast(args, static () => new { id = default(string), cancellationToken = default(CancellationToken) });
    var sql = cmd.CommandText;          // CS0103: 'cmd' does not exist (method takes only `args`)
    var commandType = cmd.CommandType;  // CS0103: same
    if (Include(sql, commandType, "id")) { }
    return typed.cancellationToken;
    if (Include(sql, commandType, "cancellationToken")) { }   // CS0162: unreachable after return
}
```

Same bug reproduced on **both Dapper.AOT 1.0.48 and 1.0.52** — longstanding (not a recent regression). The generator copy-pasted scope from `AddParameters(in UnifiedCommand cmd, object? args)` but forgot that `GetCancellationToken` only receives `args`.

Related upstream: [issue #169 (open)](https://github.com/DapperLib/DapperAOT/issues/169) — "False Positive DAP018 When Passing CancellationToken via Params Object" — same surface, different symptom.

## The actual blocker — issue #168 (filed by Harol-Reina 2026-03-16, 0 comments)

[Issue #168 — `PublishAot=true` fails because ILC still scans base Dapper.dll](https://github.com/DapperLib/DapperAOT/issues/168) is the user's own prior-session work documenting the meta-blocker:

> Dapper.AOT interceptors correctly replace all `SqlMapper` calls at compile time, **so the base Dapper code is never executed at runtime**. However, ILC still scans the entire `Dapper.dll` assembly and treats the 45+ trim/AOT warnings as fatal errors.

Even if every call site in the codebase migrates to a Dapper.AOT-intercepted shape AND R10 (CommandDefinition) is fixed AND R11 (CT-in-params) is fixed, **ILC will still emit the same ~50 diagnostics we captured in the [2026-05-19 baseline](2026-05-19-baseline-aot-publish.log)** because they originate inside `Dapper.dll`, not at the consumer call sites.

The user's own proposed solution from #168:

> A minimal `Dapper.Stubs` assembly that provides the same public API surface as `Dapper.dll` but with:
> 1. Empty method bodies (`throw new NotSupportedException`) — they are never called because interceptors replace all call sites
> 2. AOT annotations (`[RequiresDynamicCode]` + `[RequiresUnreferencedCode]`) on every method — ILC sees these and trims the unreachable code cleanly
> 3. Working `GridReader` base class — because `AotGridReader` extends it
> 4. Working `DbString` — because the generated `DbStringHelpers` reads its properties
> 5. `IWrappedDataReader` interface — referenced by generated `AotWrappedDbDataReader`

Comments on #168: **0**. Marc Gravell has not engaged since 2026-03-16.

## Cross-repo empirical scope (verified 2026-05-19)

| Surface | Count | Notes |
|---|---|---|
| Total Dapper call sites cross-repo | ~447 | grep `conn\.(Query\|Execute\|...)` across SDK+Pro+Platform storage packages |
| Sites in simple shape `conn.X<T>(sql, param)` | **~411** | Dapper.AOT-intercept-compatible **AS-IS** (assuming #168 is resolved) |
| Sites with `new CommandDefinition` (R10 surface) | **16** | Need refactor or hand-roll |
| Sites passing `cancellationToken: ct` to a Dapper call (R11 surface) | **18** | Subset that needs CT propagation preserved |
| Sites with `new DynamicParameters` (R9 — DAP015 surface) | **14** | Need rewrite to typed/anonymous params or hand-roll |
| Files affected by R9 or R10 or R11 (union) | **~14 of ~100** | Concentrated in: `PostgresSessionStore.cs`, `PostgresAuditStore.cs`, `RoleTemplateSeeder.cs`, `PostgresLiveQueueSnapshotStore.cs`, `SnoopChannelManager.cs`, `PostgresAuthEventStore.cs`, `PostgresConversationStore.cs`, `PostgresAgentStore.cs`, `PostgresPurgeLogStore.cs`, `PostgresClusterTransport.cs`, `PostgresIntervalSnapshotStore.cs`, `PostgresDncListStore.cs`, `PostgresCallAnalyticsStore.cs`, `PostgresCompletedSessionStore.cs` |

**Key insight:** 92% of Dapper call sites in this codebase are ALREADY in the shape Dapper.AOT can intercept. The plan-mode "5-6 weeks" panic estimate was wrong — the actual special-handling surface is ~14 files, not all 100+. **But** #168 (ILC scans Dapper.dll) blocks the AOT publish regardless of how many sites we migrate — fixing the mainstream sites without addressing #168 produces zero diagnostic improvement.

## Sample of the mainstream pattern (CT NOT propagated mid-query)

`PostgresQueueStore.cs:17-26`:

```csharp
public async Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);   // CT used here
    var row = await conn.QuerySingleOrDefaultAsync<QueueRow>(            // CT NOT passed here
        "SELECT … FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId",
        new { TenantId = tenantId.Value, QueueId = queueId.Value });
    return row?.ToQueue();
}
```

CT is used only to cancel **connection acquisition**, not mid-query. This is the pattern across all 411 mainstream sites. **Migrating these to Dapper.AOT is a NO-OP for CT semantics** — there's no regression, because they already don't propagate CT to the Dapper call.

## Strategic decision (taken 2026-05-19): pivot to Option O — Dapper.Stubs

Per deep analysis of all alternatives:

| Option | Resolves #168 | Effort | Risk | Chosen |
|---|---|---|---|---|
| **O — Build Verbara.Sdk.Dapper.Stubs** | ✅ Yes (by construction) | ~2-3 weeks | Low — user's own proven design from #168 | ✅ |
| L — Custom sqlc-style generator | ✅ Yes (eliminates Dapper) | 4-8 weeks | Medium — new code to invent + maintain |  |
| I — Hand-roll all NpgsqlCommand | ✅ Yes (eliminates Dapper) | 6-8 weeks | Low — mechanical but massive volume |  |
| G — Drop CT universal | ❌ No (#168 unaffected) | 1-2 weeks | Low | (rejected — doesn't unblock) |
| A original — Refactor universal | ❌ No (#168 unaffected) | 5-6 weeks | High (R11 bug in canonical pattern) | (rejected — doesn't unblock) |
| H — Wait upstream | ❌ No (timeline indefinite) | (waiting) | Very high — #168 has 0 comments in 2 months, #153 unmerged 12 months | (rejected — Marc has not engaged) |
| N — Private image distribution | (defers AOT entirely) | 0 | Low (defers the directive) | (kept as parallel mitigation while D ships) |

## Files touched + clean state at session end

| File | Action | State |
|---|---|---|
| `Verbara.Sdk/Directory.Packages.props` | Edited (Dapper.AOT) → reverted | clean on main |
| `Verbara.Sdk/src/.../Verbara.Sdk.Sessions.Postgres.csproj` | Edited → reverted | clean on main |
| `Verbara.Sdk/src/.../AssemblyInfo.cs` | Created → deleted | does not exist |
| Verbara.Sdk branch `feat/phase-d-dapper-aot-spike` | Used for experiments | DELETED (evidence in this doc) |
| `Verbara.Platform/docs/plans/active/2026-05-19-phase-d-dapper-aot.md` | TO BE AMENDED (Option O pivot) | next commit |
| `Verbara.Platform/docs/specs/2026-05-19-phase-d-dapper-aot-migration-design.md` | TO BE AMENDED (Option O pivot) | next commit |
| `Verbara.Platform/docs/operations/phase-d-validation/2026-05-19-day-1-findings.md` | NEW (this file) | next commit |

## Handoff for next session — Option O execution

Start with brainstorming **Verbara.Sdk.Dapper.Stubs** concrete design. Inputs:

1. **Issue #168 body** (user's own prior-session design): https://github.com/DapperLib/DapperAOT/issues/168
2. **Public API surface of `Dapper.dll`** (must be mirrored 1:1 in stubs)
3. **`Dapper.AOT` generated interceptors** (must compile against the stubs) — sample in `obj/Debug/generated/Dapper.AOT.Analyzers/.../*.generated.cs` after redoing the spike with stubs in place
4. **The 14 special-handling files** listed above — case-by-case decisions (hand-roll vs simple-shape refactor)

Suggested fresh-context kickoff prompt:

> Execute Option O of Phase D per `docs/operations/phase-d-validation/2026-05-19-day-1-findings.md`. Brainstorm the `Verbara.Sdk.Dapper.Stubs` project structure: which Dapper public types to mirror, which to provide working implementations for (per #168 list: `GridReader`, `DbString`, `IWrappedDataReader`, `ICustomQueryParameter`), which to stub as `throw NotSupportedException` with `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]`. Then implement, test against PostgresSessionStore as canary, AOT publish to verify diagnostic count drops to 0. Use the `dapper-aot-migration` subagent if helpful.
