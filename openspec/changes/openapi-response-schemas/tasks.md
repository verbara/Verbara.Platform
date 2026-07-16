# Tasks: openapi-response-schemas

> Ordered by dependency. Group inventories (Phase A of each group) resolve the file→group membership
> open questions (design OQ2). The manifest-completion tasks (Group 2) are **BLOCKING before
> `/xr:propagate`** — an empty manifest group at propagate time is a T11 fixture-completeness finding.

## 1. Foundation — group inventory & emitted-name capture

- [ ] 1.1 Enumerate `src/Verbara.Platform.Api/Endpoints/**/*.cs` and assign each file to a group
  (`admin-remainder`, `agent`, `analytics`, `operations`), resolving spanning files (design OQ2);
  record the assignment. **A propose-stage `candidateFiles` seed already exists per group in
  `response-schema-manifest.v1.json` — confirm/finalize it, don't restart from scratch.**
- [ ] 1.2 For each group, identify the success-response DTO record(s) per primary handler and confirm
  each is registered in `ApiJsonContext` (`[JsonSerializable]`); note any `TypeInfoPropertyName` /
  `[JsonPropertyName]` that changes the emitted schema/field name.
- [ ] 1.3 Run the host locally with `Platform:OpenApi:Enabled=true`, capture `/openapi/v1.json`, and
  record the EMITTED schema name for each identified DTO (C# record name may differ — design D3).
- [ ] 1.4 During inventory, flag any handler whose success shape cannot be expressed as a single
  `Ok<TDto>` (polymorphic/union/stream/runtime-selected success) — resolve design OQ1 per group;
  leave such handlers untyped and record them, do NOT force-fit.

## 2. Complete the response-schema manifest (BLOCKING before /xr:propagate)

- [ ] 2.1 Fill `fixtures/response-schema-manifest.v1.json` group `admin-remainder` with each emitted
  schema name + verbatim field names (from the captured document, cross-checked to `ApiJsonContext`);
  set `status` to reflect completion.
- [ ] 2.2 Fill group `agent` likewise.
- [ ] 2.3 Fill group `analytics` likewise.
- [ ] 2.4 Fill group `operations` likewise.
- [ ] 2.5 Verify no group remains `TO-COMPLETE-BY-HOST`; the `csat` group stays as the proven seed.

## 3. Convert handlers to the typed-result pattern (phased by consumer need)

- [ ] 3.1 **Phase 1 — `admin-remainder`:** convert the group's handlers from
  `Task<IResult>` / `Results.Ok(...)` / `Results.Json(...)` to `Task<Results<Ok<TDto>, ...>>` +
  `TypedResults.*`, preserving every status path and wire body byte-for-byte.
- [ ] 3.2 **Phase 2 — `agent`:** convert likewise.
- [ ] 3.3 **Phase 3 — `analytics`:** convert likewise.
- [ ] 3.4 **Phase 4 — `operations`:** convert likewise.
- [ ] 3.5 Confirm no DTO was removed from `ApiJsonContext` and no anonymous `new {}` response bodies
  were introduced.

## 4. Extend the verification script

- [ ] 4.1 Extend `scripts/verify-openapi-fixture.py` to iterate the manifest's groups/schemas and
  assert each declared schema exists in the captured document's `components/schemas` under its emitted
  name with verbatim field names (both directions; type families compared, formats not) — subsuming
  the existing single-`CsatResponseDto` check via the `csat` group.
- [ ] 4.2 Wire the CI verify step (`.github/workflows/ci.yml`) to run the manifest-driven assertion
  against the captured `openapi-document.json` (the capture/upload plumbing already exists,
  `ci.yml:95-158` — no new plumbing).

## 5. Verification

- [ ] 5.1 `dotnet build Verbara.Platform.slnx -c Release` — zero warnings (`TreatWarningsAsErrors`).
- [ ] 5.2 `dotnet test Verbara.Platform.slnx` green — endpoint/integration tests confirm wire-body
  and status-code parity for converted handlers.
- [ ] 5.3 AOT publish of `Verbara.Platform.Api` clean — no `IL2026`/`IL3050`/`IL207x` diagnostics.
- [ ] 5.4 `verify-openapi-fixture.py` passes against the captured document for every completed
  manifest group.
- [ ] 5.5 `openspec validate openapi-response-schemas --strict` and `openspec validate --all --strict`
  green; CI green.
