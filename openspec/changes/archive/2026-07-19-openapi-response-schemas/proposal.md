---
tier: GRANDE
owner: Harol
approver: Harol
stakeholder: Platform.Web frontend team (typed-client consumer), Platform API maintainers
decision_ref: Platform/ADR-0035
---

# Proposal: openapi-response-schemas (Platform host — named response schemas for typed-client consumption)

## Why

Platform's OpenAPI document emits almost no named top-level response DTOs, so Web's typed-client
migration is capped at 6 of 44 target hooks: the generated `openapi.d.ts` has no `components/schemas`
entry to point a `customFetch<T>` at for the other 38. Root cause (verified by the Platform scout):
~415 handlers across ~77 endpoint files return untyped `Task<IResult>` via `Results.Ok(...)` /
`Results.Json(...)` with zero `.Produces<T>()` metadata; the **only** pattern in the codebase that
surfaces a named `components/schemas` response entry is `Task<Results<Ok<T>, ...>>` +
`TypedResults.Ok<T>` — present today only in
`src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs:282,347` and
`CsatTemplateAdminEndpoints.cs` (the ADR-0035 seed). This change extends that proven pattern to the
handler groups Web actually consumes, so named response schemas surface for codegen.

## What Changes

- **Convert endpoint handlers to the typed-result pattern** — replace untyped `Task<IResult>` /
  `Results.Ok(...)` / `Results.Json(...)` success returns with
  `Task<Results<Ok<TDto>, ...>>` + `TypedResults.*`, so each success DTO surfaces as a named
  `components/schemas` entry in `/openapi/v1.json`. **PHASED by consumer need**, not a big-bang
  415-handler rewrite: the groups Web actually consumes — `admin-remainder`, `agent`, `analytics`,
  `operations` (the manifest groups) — in that consumer-driven order. Exact group inventories,
  per-group handler counts, and phase boundaries are **design-stage decisions** (see `design.md`),
  not fixed here.
- **Complete the cross-repo response-schema manifest**
  (`fixtures/response-schema-manifest.v1.json`) — fill every `TO-COMPLETE-BY-HOST` group
  (`admin-remainder`, `agent`, `analytics`, `operations`) with the **EMITTED** schema name +
  **verbatim** field names, read from the actual DTO records registered in `ApiJsonContext`. This is
  a **BLOCKING task before `/xr:propagate`**: an empty group at propagate time is a
  fixture-completeness finding (T11, verbatim-fixture-citation rule). The manifest records emitted
  schema names, which can differ from the C# record name (`CsatAggregateDto` vs the emitted
  `CsatResponseDto` — the naming lesson already captured in the fixture's `$comment`).
- **Extend the verification script** — `scripts/verify-openapi-fixture.py` (today asserting only the
  single `CsatResponseDto` fragment) grows to assert the completed manifest against the CI-captured
  document: every declared schema exists in `components/schemas` with the declared verbatim field
  names, per group.
- No changes to response bodies at runtime — the JSON on the wire stays byte-identical
  (`TypedResults.Ok(dto)` serializes the same `dto` as `Results.Ok(dto)`); this change adds
  schema **metadata**, not new behavior.

## Capabilities

### New Capabilities

- `typed-response-schemas`: the requirement that the consumer-facing endpoint groups return the
  typed `Results<Ok<TDto>, ...>` shape with `TypedResults.*` so named response schemas surface in
  the emitted OpenAPI document, phased by consumer need, with wire-body parity and AOT-safety as
  invariants.

### Modified Capabilities

- `openapi-export`: adds the requirement that the completed multi-group
  `response-schema-manifest.v1.json` is verified against the CI-captured document (extending the
  existing single-`CsatResponseDto`-fragment check to a per-group, per-schema, verbatim-field-name
  assertion).

## Impact

- **Code:** `src/Verbara.Platform.Api/Endpoints/**` handler signatures + return statements for the
  four consumer groups (phased); `scripts/verify-openapi-fixture.py` (manifest-driven assertion).
  No DTO shape changes; every DTO already lives in `ApiJsonContext` and stays there. No runtime
  endpoint added or removed; no gating change to `/openapi/v1.json` / `/scalar/v1`.
- **APIs:** no request/response contract change — response bodies are byte-identical on the wire;
  only the OpenAPI document's `components/schemas` gains named entries.
- **Fixtures:** `fixtures/response-schema-manifest.v1.json` completed (four groups) — the
  verbatim-fixture-citation source for all downstream children.
- **Dependencies:** none on Sdk/Pro. Downstream consumer: Web's `web/openapi-response-adoption`
  child (buildOrder 2 per `impact.yaml`) regenerates `openapi.d.ts` and migrates the admin
  remainder; the three pre-existing HELD Web children (`openapi-typed-client-agent`, `-analytics`,
  `-operations`) un-gate on this thread and run as their own backlog items. Web-side work is
  **out of scope** for this host change.
