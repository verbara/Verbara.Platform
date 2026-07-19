# Design: openapi-response-schemas

## Context

The cross-repo `openapi-typed-client` train (Platform/ADR-0035) gave Web a real OpenAPI document to
generate types from, and proved the end-to-end path with `CsatResponseDto`
(`use-analytics.ts:523`). But Web's migration stalled at 6/44 hooks because Platform emits almost no
named top-level response schemas. Two read-only scouts established the boundary (cited, not
re-derived here):

- **Platform scout (producer):** ~415 handlers across ~77 files under
  `src/Verbara.Platform.Api/Endpoints/**` return untyped `Task<IResult>` via `Results.Ok(...)` /
  `Results.Json(...)` with zero `.Produces<T>()`. The ONLY emitting pattern in the codebase is
  `Task<Results<Ok<T>, ...>>` + `TypedResults.Ok<T>`
  (`CsatResponseEndpoints.cs:282,347`, `CsatTemplateAdminEndpoints.cs`). The OpenAPI pipeline is
  plain `AddOpenApi()` (`Program.cs:1625-1631, 1743-1748`) with **no transformers**. The
  capture/verify pipeline (`ci.yml:95-158`, `scripts/verify-openapi-fixture.py`) needs no new
  plumbing — only manifest extension.
- **Web scout (consumer, verdict: covered):** the consumption machinery is fully built —
  `scripts/generate-api-types.mjs` emits every `components/schemas` entry on regenerate,
  `src/core/api/generated/openapi.d.ts` is the single seam, `customFetch<T>` at
  `src/core/api/client.ts:222` is the swap-the-`T` contract. The 3 HELD Web children plus the
  admin remainder (38 files) un-gate when this host change ships named schemas.

This is a **producer-side** change: make the response DTOs *nameable* in the emitted document. It is
authored under this repo per the hub rule (Platform is the authoritative workstream for
Platform + Platform.Web), but touches only Platform's API host.

## Goals / Non-Goals

**Goals:**
- Surface named `components/schemas` response entries for the four consumer-facing endpoint groups
  by extending the proven `TypedResults` pattern, phased by consumer need.
- Complete `response-schema-manifest.v1.json` as the verbatim-fixture-citation contract for all
  downstream children, and extend `verify-openapi-fixture.py` to enforce it in CI.
- Preserve wire-body parity, AOT-compatibility, and the warning-clean build absolutely.

**Non-Goals:**
- No new endpoints, no DTO shape changes, no changes to request contracts, status codes, or gating.
- No Web-side work — that is the `web/openapi-response-adoption` consumer child (buildOrder 2).
- No OpenAPI document transformers — the typed-signature route is the mechanism, UNLESS a specific
  handler class proves inexpressible that way (recorded as an open question below, not assumed).
- No big-bang rewrite of all ~415 handlers — only the consumer-driven groups, phased.

## Decisions

### D1: Extend the typed-result signature, do NOT add a document transformer

`Task<Results<Ok<TDto>, ...>>` + `TypedResults.Ok<TDto>` is the one pattern already proven to make
`Microsoft.AspNetCore.OpenApi`'s schema generator name the response type in `components/schemas`.
The alternative — keeping `Task<IResult>` and adding `.Produces<TDto>()` metadata or an
`IOpenApiOperationTransformer` — was rejected: it duplicates the type in two places (the return
statement and the metadata), is easy to drift, and the codebase has *zero* transformers today
(keeping it that way is a smaller, more auditable surface). The typed signature makes the compiler
the single source of truth for the response type, which is exactly the drift-proofing this whole
train exists to buy (the csat-runner incident was hand-transcription drift).

### D2: Phase by consumer need, group-by-group

Convert in the order Web consumes: `admin-remainder` (the 38-file remainder Web's first child
migrates), then `agent`, `analytics`, `operations`. Each group is an independently shippable phase
(its schemas emit, its manifest group completes, `verify-openapi-fixture.py` asserts it). This keeps
each phase's blast radius small and lets the Web children un-gate incrementally rather than waiting on
a 415-handler mega-PR. **The concrete group inventories (which files map to which group) and the
per-phase handler lists are decided at this design stage from the endpoint tree**, not fixed in the
proposal — the four group names are the fixed contract; their membership is derived.

### D3: The manifest is the cross-repo contract; completing it is BLOCKING before propagate

`response-schema-manifest.v1.json` records, per group, the EMITTED schema name + verbatim field names
(read from the actual DTO records in `ApiJsonContext`, confirmed against the captured document).
Every `TO-COMPLETE-BY-HOST` group MUST be filled before `/xr:propagate` — an empty group at propagate
time is a blocking fixture-completeness finding (T11). The manifest records EMITTED names, which can
differ from the C# record name (`CsatAggregateDto` → emitted `CsatResponseDto`); the completion task
verifies each declared name against the real captured document, never trusting the C# name blind.

### D4: Extend the existing verifier, keep its format-tolerance

`verify-openapi-fixture.py` today asserts one hard-coded `CsatResponseDto` fragment. It grows to
iterate the manifest's groups/schemas and assert each against `components/schemas`: field names
verbatim (both directions — missing and extra both fail), type families compared (string/number/
integer, tolerating the .NET 10 integer/string big-number union), numeric/date-time formats NOT
compared (unchanged from today's documented intent, so servicing updates don't re-fail the check).
The existing single-fragment behavior is subsumed by the `csat` group entry (already `proven`).

## Risks / Trade-offs

- **A handler returns a shape not cleanly expressible as `Ok<TDto>`** (e.g. polymorphic/union
  bodies, raw streams, `IResult` chosen at runtime among differently-typed successes) → converting it
  to a single `Ok<TDto>` would misrepresent or change the contract. **Mitigation:** such a handler is
  left untyped for now and recorded as an open question (see below); it is NOT force-fit, and a
  document transformer is reconsidered only if a whole *class* of such handlers blocks a group. This
  bounds the change to the safe majority.
- **Wire-body drift during conversion** → a mechanical `Results.Ok(x)` → `TypedResults.Ok(x)` swap is
  body-preserving, but a careless refactor could alter a status path. **Mitigation:** wire-body
  parity is an explicit spec invariant; existing endpoint/integration tests guard the contracts;
  review each phase's diff for status-code/body changes.
- **AOT regression** → new generic instantiations of `Results<Ok<TDto>, ...>`. **Mitigation:**
  `TypedResults` is AOT-safe and every `TDto` is already `[JsonSerializable]` in `ApiJsonContext`
  (the source-gen context already emits its metadata); the AOT publish + `TreatWarningsAsErrors` gate
  catches any `IL2026`/`IL3050`/`IL207x` regression.
- **Emitted-name surprises** (C# record name ≠ emitted schema name) → the manifest could record a
  wrong name. **Mitigation:** D3 — every manifest entry is verified against the *real* captured
  document, not the C# source alone.

## Migration Plan

Per phase (group), following this repo's Subagent-Driven Development / FCM batching:
1. **Phase A (foundation):** inventory the group's endpoint files and their success-response DTOs;
   confirm each DTO is registered in `ApiJsonContext`; capture the emitted schema name from a local
   `/openapi/v1.json` run.
2. **Phase B (critical):** convert the group's handler signatures + return statements to
   `Task<Results<Ok<TDto>, ...>>` + `TypedResults.*` (focused subagents per file cluster); fill the
   group in `response-schema-manifest.v1.json` with emitted name + verbatim fields.
3. **Phase C (integration):** extend `verify-openapi-fixture.py` (once) to assert the manifest; run
   the captured-document verification; `dotnet test` green, `dotnet build -c Release` warning-clean,
   AOT publish clean.

Rollback: the change is additive metadata (typed signatures + manifest + verifier); reverting a
phase's endpoint diff restores the untyped handlers with no wire-body change to unwind.

## Open Questions

- **OQ1 (to be resolved during Phase A of each group):** does any handler class in the four groups
  return a success shape that cannot be expressed as a single `Ok<TDto>` (polymorphic/union success
  bodies, raw file/stream results, runtime-selected differently-typed successes)? If found, record
  the specific handler(s), leave them untyped, and decide per-class whether a bounded document
  transformer is warranted — this is the only condition under which the Non-Goal "no transformers"
  is revisited. (No such class is known at propose time; this is a design-stage discovery task.)
- **OQ2:** exact file→group membership for `admin-remainder` vs `agent` vs `analytics` vs
  `operations` where an endpoint file spans concerns — resolved from the endpoint tree at Phase A,
  recorded in the manifest's group assignments. A propose-stage inventory pass has already seeded a
  `candidateFiles` list per group in `response-schema-manifest.v1.json` (verified from the endpoint
  tree); Phase A confirms and finalizes membership for spanning files (`TypificationEndpoints.cs`
  splits between admin-schema and runtime `/typify`; `SkillEndpoints.cs` is shared agent/operations).
- **OQ3 (resolved at propose stage — recorded for the reader):** the four manifest groups are
  deliberately left `TO-COMPLETE-BY-HOST` by this proposal, NOT filled with schema names now.
  A named response schema for a group's DTO only appears in `/openapi/v1.json` **after** that handler
  is converted (tasks.md Group 3), and the emitted schema name can differ from the C# record name
  (the `CsatAggregateDto` → emitted `CsatResponseDto` lesson). The propose inventory read C# source,
  which yields PascalCase property names and C# type names — NOT the emitted camelCase field names or
  emitted schema names. Freezing those source-derived guesses into the manifest as if
  verbatim-verified would reintroduce exactly the guess-the-DTO failure the verbatim-fixture-citation
  rule (T11) exists to prevent (csat-runner, Web PR#159). Manifest completion is therefore a
  BLOCKING apply-stage task (tasks.md Group 2), verified against the real captured document, gated
  before `/xr:propagate` — consistent with the seed manifest's own `$comment` ("MUST complete …
  BEFORE /xr:propagate").
