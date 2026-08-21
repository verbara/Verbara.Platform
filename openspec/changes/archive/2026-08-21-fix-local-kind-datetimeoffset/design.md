## Context

The proposal frames this as ~12 row projections constructing `new DateTimeOffset(x, TimeSpan.Zero)`
over a `Local`-kind value. Investigation found the count is **54** sites, and — more importantly —
that they are all a *symptom*. The proposal's own "establish why the kind is `Local` at all"
requirement resolves to a single line:

```xml
<!-- src/Verbara.Platform.Api/Verbara.Platform.Api.csproj:52 -->
<RuntimeHostConfigurationOption Include="Npgsql.EnableLegacyTimestampBehavior" Value="true" Trim="false" />
```

`Npgsql.EnableLegacyTimestampBehavior` is a **process-wide** feature switch. Under it, Npgsql
selects `LegacyDateTimeOffsetConverter` / legacy `DateTimeConverterResolver`, and reading a
`timestamptz` yields a `DateTime` with `Kind=Local` **converted to the machine's local time**.
Without it, the same read yields `Kind=Utc`. That is the entire mechanism: on a UTC runner
`Local` and `Utc` share offset zero so nothing throws, and on a UTC-5 host every one of the 54
sites throws.

This was verified empirically, not from documentation — a console app pinned to Npgsql 10.0.3 run
twice against a real Postgres with `TZ=America/Bogota`, once plain and once with the identical
`RuntimeHostConfigurationOption` the Api csproj declares:

| | LEGACY (today) | MODERN (switch removed) |
|---|---|---|
| `GetDateTime` on `timestamptz` | `Kind=Local` | `Kind=Utc` |
| `new DateTimeOffset(dt, TimeSpan.Zero)` | **throws** | OK |
| write `DateTimeOffset` with `Offset=-05:00` | OK (silently normalised) | **throws** |
| stored instant, all Platform write paths | correct | correct (byte-identical) |
| `infinity`, `DateOnly`, `timestamptz[]` | — | identical |

Four facts constrain the approach:

1. **The schema is 215 `timestamptz` columns to 6 plain `timestamp`.** Under MODERN, reads yield
   `Kind=Utc` and all 54 sites become correct by construction. (The 6 plain-`timestamp` columns are
   not in this repo's schema.)
2. **MODERN is already the majority configuration against the same database.** Only 1 of the 4
   executable hosts declares the switch. `Verbara.Platform.Mail` (OAuth `TokenStore`) and
   `Verbara.Platform.Realtime` (`Pro.Cluster.Postgres` leader election, TTL-renewal heavy) run
   MODERN in production today, as does the entire test suite — no test project declares the switch.
3. **The test suite is therefore blind by construction, in a way `TZ` alone does not fix.** Tests
   run MODERN while the host runs LEGACY, so a regression test that only sets a non-UTC `TZ` would
   pass *vacuously*. This is a stronger blindness than the proposal's "CI runners are UTC".
4. **The switch was never a fix.** `git log -S EnableLegacyTimestampBehavior --all` yields one
   introducing commit — `1f0e5211` (2026-04-01), *"fix: demo Docker compatibility — permissions, DI,
   Npgsql, licensing"* — a grab-bag expedient with no ADR, no openspec change, no design note and no
   test. It was carried forward verbatim through the Native AOT flip (`4e90281c`, Platform/ADR-0022
   Phase D) purely as a mechanism change (`runtimeconfig.template.json` → `RuntimeHostConfigurationOption`).

## Goals / Non-Goals

**Goals:**

- Remove the process-wide dependence on legacy Npgsql timestamp semantics, so no projection,
  worker, or endpoint depends on the host's timezone.
- Keep every persisted instant byte-identical across the flip — no data migration.
- Make `POST /api/v1/setup` retryable after a mid-way failure.
- Leave behind coverage that fails under the host's *real* switch state, not a vacuous `TZ` test.

**Non-Goals:**

- Re-auditing every `DateTime` in the codebase (the proposal already scopes this out).
- Changing any SQL schema, HTTP route, or DTO shape.
- Publishing a `Verbara.Sdk` / `Verbara.Sdk.Pro` release. The Pro-side exposure identified below is
  fixed entirely from the Platform side.
- Adopting `DateOnly`/`TimeOnly` or changing `IClock`.

## Decisions

### D1 — Remove the switch; do not patch the 54 sites

Deleting the `ItemGroup` at `Verbara.Platform.Api.csproj:50-53` fixes all 54 sites at once, because
`Kind=Utc` makes `new DateTimeOffset(x, TimeSpan.Zero)` legal by construction.

*Alternative considered — keep the switch and normalise at all 54 sites (the proposal's stated
approach). Rejected on three grounds:*

- It leaves the class alive: any new projection reintroduces the defect, and the host keeps a
  timezone dependence the tests structurally cannot see.
- **It is actively unsafe as written.** The proposal offers `DateTime.SpecifyKind(x, Utc)` and
  `x.ToUniversalTime()` as two acceptable uniform choices. Under LEGACY they are *not* equivalent
  and only `ToUniversalTime()` is correct — `SpecifyKind` relabels an already-local-converted value
  as UTC, shifting the instant by the host's offset. The repo's single existing `SpecifyKind` site,
  `PostgresBotConfigStore.cs:119`, is corrupting `CreatedAt` on non-UTC hosts today. Applying the
  proposal's first option uniformly would spread that corruption to all 54 sites.
- It does not fix the write-side regression D2 describes, which stays masked rather than resolved.

*Alternative considered — set `TZ=UTC` in the container and document it. Rejected:* it is the
workaround the proposal already identifies as undiscoverable, it does nothing for host-run binaries
or dev machines, and it makes correctness depend on deployment configuration rather than on code.

### D2 — Removal MUST ship together with UTC normalisation at every untrusted `DateTimeOffset` ingress

This is the one real regression removal introduces, and it was not visible from the proposal.
MODERN's `DateTimeOffsetConverter.WriteCore` rejects **any** `DateTimeOffset` whose `Offset` is
non-zero — even with an explicit `NpgsqlDbType.TimestampTz`:

> `Cannot write DateTimeOffset with Offset=-05:00:00 to PostgreSQL type 'timestamp with time zone',
> only offset 0 (UTC) is supported.`

`LegacyDateTimeOffsetConverter.WriteCore` has no such check — it calls `.UtcDateTime` for you. The
domain model here is `DateTimeOffset`-first, and today **no** ingress path normalises, so removing
the switch alone would break writes and range-filtered reads.

The trigger is more common than an explicitly-suffixed offset: `DateTimeOffset.Parse("2026-08-20T10:00:00")`
on a string carrying *no* offset stamps the **machine-local** offset. On a UTC-5 host, ordinary
offset-less client input breaks.

Confirmed ingress classes (30 `Parse`/`TryParse` sites plus the DTO/query surface):

| Path | Example |
|---|---|
| `[FromBody]` DTO field | `CsatResponseRequest.CapturedAt` (anonymous public endpoint), `CreateRateCardRequest`, `GenerateInvoiceRequest`, `PromoGrantRequest`, `AddDncEntryRequest` |
| `[FromQuery] DateTimeOffset?` | `AuditEndpoints`, `AuthAdminEndpoints`, `GdprEndpoints`, `PartnerBillingEndpoints`, `CreditLedgerEndpoints` |
| `DateTimeOffset.Parse`/`TryParse` on a query string | `AnalyticsEndpoints:48-49`, `CampaignEndpoints:140,141,444,553,554` |

ASP.NET Core does **not** normalise these for you: its `DateTimeOffset` binder uses
`DateTimeStyles.AllowWhiteSpaces | AssumeUniversal`, which only supplies a *missing* offset — an
explicit `-05:00` survives verbatim into the parameter.

### D3 — Normalise at ingress, not at the bind site

Two placements were considered for the `.ToUniversalTime()` sweep.

*Bind-site* (every `NpgsqlParameter` construction) is the more defensive shape, but it is ~103
temporal binds in Platform **and cannot reach the ~124 binds inside `Verbara.Sdk.Pro`'s compiled
Postgres stores**, which Platform feeds directly (Dialer campaign create/update, DNC entries,
campaign callbacks, and every analytics range query).

*Ingress* — normalising where the offset originates — is a smaller surface (~30 `Parse`/`TryParse`
sites plus the DTO/query params) and covers **both** Platform's own stores and Pro's compiled ones
with a single rule. Chosen for that reason: it is the only placement that fixes the cross-boundary
exposure without a Pro release.

The rule is `.ToUniversalTime()`, never `SpecifyKind` — see D1. `ToUniversalTime()` on an
already-UTC value is a no-op, so it is safe to apply unconditionally.

### D4 — Guard the invariant in CI, not just in a test

The repo already runs an **Invariant Gates** job driven by `scripts/check-endpoint-invariants.py`.
Extend it with two static checks:

1. No `.csproj`, `runtimeconfig.template.json`, or `runtimeconfig.json` may declare
   `Npgsql.EnableLegacyTimestampBehavior` — the switch cannot come back silently.
2. No `DateTime.SpecifyKind(..., DateTimeKind.Utc)` may wrap a value read from a Postgres reader —
   the corrupting pattern from D1.

This is cheaper and more durable than a runtime test, and it fails on a UTC CI runner, satisfying
the spec's "fails on a UTC CI runner too" scenario.

### D5 — Regression coverage must exercise the host's real converter selection

A test that only sets `TZ` proves nothing while test projects run MODERN (Context §3). Coverage
must therefore assert the *post-removal* contract:

- A Storage.Postgres round-trip under a non-UTC `TZ` asserting the read yields `Kind=Utc` / `Offset=0`
  and that the projection does not throw.
- A write test binding a `DateTimeOffset` with a non-zero `Offset` through an ingress path,
  asserting it is normalised rather than rejected (the D2 contract).
- Once the switch is gone, host and tests share one semantics, so these tests are meaningful rather
  than vacuous — which is itself the structural fix.

### D6 — `/setup` retryability: move the guard to the platform user

Independent of the timezone work. `SetupEndpoints.cs:31-33` guards on
`tenantStore.GetHostTenantAsync(ct)` — the host tenant is written first (step 1 of 6), so any later
failure leaves the sentinel behind and the retry returns `409`.

Chosen: base the guard on evidence that setup actually *finished* — the existence of a platform
**user** — which the spec explicitly permits. Rationale over the transactional alternative: the six
writes span multiple stores with no shared transaction seam (`ITenantStore`, `IUserStore`,
`IApiKeyStore`, `ITenantRoleStore`, `IUserRoleStore`), so making them atomic is a storage-layer
redesign well beyond a `PEQUEÑO` change. Re-running setup over a half-written state is naturally
idempotent for the tenant rows (same deterministic `platform` id).

## Risks / Trade-offs

- **Missing an ingress site leaves a latent 500 on a non-UTC host.** → The sweep is enumerable
  (`Parse`/`TryParse` + `DateTimeOffset`-typed DTO/query params), D4 gate blocks reintroduction, and
  D5 covers the contract. Residual risk is a path with no test and no client sending an offset.
- **Wire-format change on non-UTC hosts.** `DateTimeOffset`-valued response fields serialise as
  `…-05:00` today and `…+00:00` after removal. Same instant, different string; ISO-8601 consumers
  handle both, and it is a **no-op on UTC containers**, which is every shipped image. Called out
  because `Verbara.Platform.Web` renders these directly.
- **The flip is process-wide and silently re-qualifies Pro's ~124 temporal binds.** → Pro contains
  zero `DateTime`-typed timestamp properties, zero `GetDateTime` call sites, zero local-time
  constructs, and its full Postgres integration suite already runs MODERN against real Postgres and
  passes. Exposure is limited to values *Platform* hands it — which D2/D3 fix.
- **Scope is materially larger than the proposal's `tier: PEQUEÑO`.** Removal plus an ingress sweep
  plus a CI gate plus the `/setup` guard is `MEDIANO` work. Recorded here; the frontmatter should be
  updated when tasks are cut.
- **`Trim="false"` / AOT is a non-issue.** Npgsql ships no `ILLink.Substitutions.xml` and no
  `build/` folder, so the switch is not folded at trim time and deleting the `ItemGroup` is
  AOT- and trim-neutral.

## Migration Plan

No data migration is required — the strongest possible objection, and it does not apply. Every
Platform write to `timestamptz` stores the identical correct UTC instant under **both** switch
states (verified byte-identical in the probe across `Kind=Utc`, typed and untyped binds, and
`Offset=0` `DateTimeOffset`s). The bytes on disk are already right; only the read-side `Kind` and
the write-side acceptance rule change.

1. Land the ingress normalisation sweep (D2/D3) **first**, while the switch is still on. It is a
   no-op under LEGACY (`.ToUniversalTime()` before a converter that would have called `.UtcDateTime`
   anyway), so it is independently safe and independently revertable.
2. Remove the `ItemGroup` (D1). All 54 sites become correct at this commit.
3. Review `PostgresBotConfigStore.cs:119` — the lone `SpecifyKind` becomes correct automatically
   under MODERN, but it should be simplified rather than left as a misleading artefact.
4. Add the invariant gate (D4) and the regression coverage (D5).
5. `/setup` guard (D6) — orthogonal, can land in any order.

**Rollback:** restore the four-line `ItemGroup`. The ingress sweep stays safe under LEGACY, so
rollback does not require reverting steps 1 or 3–5.

**Deploy note:** operators currently running a non-UTC host with `TZ=UTC` set as a workaround can
drop it after this ships; leaving it set remains harmless.

## Open Questions

- Should the D4 invariant gate also reject *new* `new DateTimeOffset(x, TimeSpan.Zero)`
  constructions over reader-sourced values? Under MODERN they are correct, so banning them is
  defensive rather than necessary. Deferrable — it changes neither the specs, the approach, nor the
  task breakdown.
